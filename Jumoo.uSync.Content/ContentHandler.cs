﻿using System;
using System.IO;
using System.Xml.Linq;
using System.Collections.Generic;

using Jumoo.uSync.Core;
using Jumoo.uSync.Core.Extensions;

using Jumoo.uSync.BackOffice;
using Jumoo.uSync.BackOffice.Helpers;

using Umbraco.Core;
using Umbraco.Core.Models;
using Umbraco.Core.Services;
using Umbraco.Core.Logging;
using System.Linq;

using Jumoo.uSync.Content.UrlRedirect;

namespace Jumoo.uSync.Content
{
    public class ContentHandler : BaseContentHandler<IContent>, ISyncHandler, ISyncHandlerConfig
    {
        public string Name { get { return "uSync: ContentHandler"; } }
        public int Priority { get { return uSyncConstants.Priority.Content; } }
        public string SyncFolder { get { return "Content"; } }

        private List<uSyncHandlerSetting> _settings;

        private bool _exportRedirects;
        private RedirectSerializer _redirectSerializer;

        public ContentHandler() :
            base("content")
        { }

        public void LoadHandlerConfig(IEnumerable<uSyncHandlerSetting> settings)
        {
            LogHelper.Info<ContentHandler>("Loading Handler Settings {0}", () => settings.Count());
            _settings = settings.ToList();

            _exportRedirects = true;

            // load up the redirect serializer (it's not part of core) 
            if (_exportRedirects && uSyncCoreContext.Instance.Serailizers.Any(x => x.Key == "RedirectSerializer"))
            {
                if (uSyncCoreContext.Instance.Serailizers["RedirectSerializer"] is RedirectSerializer)
                {
                    _redirectSerializer = (RedirectSerializer)uSyncCoreContext.Instance.Serailizers["RedirectSerializer"];
                }
            }
        }

        public override SyncAttempt<IContent> Import(string filePath, int parentId, bool force = false)
        {
            LogHelper.Debug<ContentHandler>("Importing Content : {0} {1}", ()=> filePath, ()=> parentId);
            if (!System.IO.File.Exists(filePath))
                throw new FileNotFoundException(filePath);

            var node = XElement.Load(filePath);
            return uSyncCoreContext.Instance.ContentSerializer.Deserialize(node, parentId, force);
            
        }

        public override void ImportSecondPass(string file, IContent item)
        {
            // uSyncCoreContext.Instance.ContentSerializer.D
            XElement node = XElement.Load(file);
            uSyncCoreContext.Instance.ContentSerializer.DesearlizeSecondPass(item, node);
        }

        public override SyncAttempt<IContent> ImportRedirect(string file, bool force = false)
        {
            if (_exportRedirects && _redirectSerializer != null)
            {
                LogHelper.Debug<ContentHandler>("Importing Redirects: {0}", () => file);
                if (!System.IO.File.Exists(file))
                    throw new FileNotFoundException(file);

                var node = XElement.Load(file);
                return _redirectSerializer.DeSerialize(node, force);
            }

            return base.ImportRedirect(file, force);
        }

        public IEnumerable<uSyncAction> ExportAll(string folder)
        {
            LogHelper.Info<ContentHandler>("Exporting Content");

            List<uSyncAction> actions = new List<uSyncAction>();

            foreach(var item in _contentService.GetRootContent())
            {
                actions.AddRange(ExportFolder(item, "", folder));
            }

            return actions;
        }

        private IEnumerable<uSyncAction> ExportFolder(IContent item, string path, string rootFolder)
        {

            List<uSyncAction> actions = new List<uSyncAction>();

            if (item == null)
                return actions;

            var itemPath = Path.Combine(path, item.Name.ToSafeFileName());
            // var itemPath = string.Format("{0}/{1}", path, item.Name.ToSafeFileName());

            actions.Add(ExportItem(item, itemPath, rootFolder));

            foreach (var childItem in _contentService.GetChildren(item.Id))
            {
                actions.AddRange(ExportFolder(childItem, itemPath, rootFolder));
            }

            return actions;
        }

        private uSyncAction ExportItem(IContent item, string path, string rootFolder, bool redirects = true)
        {
            if (item == null)
                return uSyncAction.Fail(Path.GetFileName(path), typeof(IContent), "item not set");

            try
            {
                var attempt = uSyncCoreContext.Instance.ContentSerializer.Serialize(item);

                string filename = string.Empty;
                if (attempt.Success)
                {
                    filename = uSyncIOHelper.SavePath(rootFolder, SyncFolder, path, "content");
                    uSyncIOHelper.SaveNode(attempt.Item, filename);

                    if (redirects)
                        ExportRedirects(item, path, rootFolder);
                }

                return uSyncActionHelper<XElement>.SetAction(attempt, filename);
            }
            catch(Exception ex)
            {
                LogHelper.Warn<ContentHandler>("Error saving Content: {0}", ()=> ex.ToString());
                return uSyncAction.Fail(item.Name, typeof(IContent), ChangeType.Export, ex);
            }
        }

        private void ExportRedirects(IContent item, string path, string rootFolder)
        {
            if (_exportRedirects && _redirectSerializer != null)
            {
                var redirectAttempt = _redirectSerializer.Serialize(item);
                if (redirectAttempt.Success && redirectAttempt.Change > ChangeType.NoChange)
                {
                    var redirectFile = uSyncIOHelper.SavePath(rootFolder, SyncFolder, path, "redirect");
                    uSyncIOHelper.SaveNode(redirectAttempt.Item, redirectFile);
                }
            }
        }

        public void RegisterEvents()
        {
            ContentService.Saved += ContentService_Saved;
            ContentService.Trashing += ContentService_Trashed;
            ContentService.Copied += ContentService_Copied;

            ContentService.Published += ContentService_Published;
        }

        private void ContentService_Published(Umbraco.Core.Publishing.IPublishingStrategy sender, Umbraco.Core.Events.PublishEventArgs<IContent> e)
        {
            if (uSyncEvents.Paused)
                return;

            if (_exportRedirects)
            {
                foreach (var item in e.PublishedEntities)
                {
                    ExportRedirects(item);
                }
            }
        }

        private void ExportRedirects(IContent item)
        {
            var path = GetContentPath(item);
            ExportRedirects(item, path, uSyncBackOfficeContext.Instance.Configuration.Settings.Folder);

            foreach(var child in item.Children())
            {
                ExportRedirects(child);
            }

        }

        private void ContentService_Copied(IContentService sender, Umbraco.Core.Events.CopyEventArgs<IContent> e)
        {
            if (uSyncEvents.Paused)
                return;

            SaveItems(sender, new List<IContent>(new IContent[] { e.Copy }));
        }

        private void ContentService_Saved(IContentService sender, Umbraco.Core.Events.SaveEventArgs<IContent> e)
        {
            LogHelper.Info<ContentHandler>("Content Save Fired:");
            if (uSyncEvents.Paused)
                return;

            SaveItems(sender, e.SavedEntities);
        }

        void SaveItems(IContentService sender, IEnumerable<IContent> items)
        {
            if (uSyncEvents.Paused)
                return;

            foreach (var item in items)
            {
                if (!item.Trashed)
                {
                    var path = GetContentPath(item);
                    var attempt = ExportItem(item, path, uSyncBackOfficeContext.Instance.Configuration.Settings.Folder, false);
                    if (attempt.Success)
                    {
                        NameChecker.ManageOrphanFiles(SyncFolder, item.Key, attempt.FileName);
                    }
                }
            }
        }

        private void ContentService_Trashed(IContentService sender, Umbraco.Core.Events.MoveEventArgs<IContent> e)
        {
            LogHelper.Info<ContentHandler>("Content Trashed:");
            foreach (var moveInfo in e.MoveInfoCollection)
            {
                uSyncIOHelper.ArchiveRelativeFile(SyncFolder, GetContentPath(moveInfo.Entity), "content");
            }
        }

        private string GetContentPath(IContent item)
        {
            var path = item.Name.ToSafeFileName();
            if (item.ParentId != -1)
            {
                path = string.Format("{0}\\{1}", GetContentPath(item.Parent()), path);
            }

            return path;
        }

        public override uSyncAction ReportItem(string file)
        {
            var node = XElement.Load(file);
            var update = uSyncCoreContext.Instance.ContentSerializer.IsUpdate(node);
            return uSyncActionHelper<IContent>.ReportAction(update, node.NameFromNode());
        }
    }
}
