﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using SmartStore.Core;
using SmartStore.Core.IO;
using SmartStore.Core.Logging;
using SmartStore.Core.Search;
using SmartStore.Services.Tasks;
using SmartStore.Utilities;

namespace SmartStore.Services.Search
{
	public class DefaultIndexingService : IIndexingService
	{
		private readonly IIndexManager _indexManager;
		private readonly IEnumerable<IIndexCollector> _collectors;
		private readonly ILockFileManager _lockFileManager;
		private readonly ICommonServices _services;
		private readonly IApplicationEnvironment _env;

		public DefaultIndexingService(
			IIndexManager indexManager,
			IEnumerable<IIndexCollector> collectors,
			ILockFileManager lockFileManager,
			ICommonServices services)
		{
			_indexManager = indexManager;
			_collectors = collectors;
			_lockFileManager = lockFileManager;
			_services = services;
			_env = services.ApplicationEnvironment;

			Logger = NullLogger.Instance;
		}

		public ILogger Logger { get; set; }

		public IEnumerable<string> EnumerateScopes()
		{
			return _collectors.Select(x => x.Scope);
		}

		public void RebuildIndex(string scope, TaskExecutionContext context)
		{
			BuildIndexInternal(scope, true, context);
		}

		public void UpdateIndex(string scope, TaskExecutionContext context)
		{
			BuildIndexInternal(scope, false, context);
		}

		public void DeleteIndex(string scope)
		{
			if (!_indexManager.HasAnyProvider())
				return;

			// TODO: error handling

			var lockFilePath = GetStatusFilePath(scope) + ".lock";

			ILockFile lockFile;
			if (!_lockFileManager.TryAcquireLock(lockFilePath, out lockFile))
			{
				// TODO: throw Exception or get out?
			}

			using (lockFile)
			{
				var provider = _indexManager.GetIndexProvider();
				var store = provider.GetIndexStore(scope);

				if (store.Exists)
				{
					store.Delete();
				}

				// TODO: delete info file
			}
		}

		private void BuildIndexInternal(string scope, bool rebuild, TaskExecutionContext context)
		{
			if (!_indexManager.HasAnyProvider())
				return;

			var collector = GetCollectorFor(scope);

			if (collector == null)
			{
				throw Error.Argument("A collector for indexing scope '{0}' does not exist.".FormatInvariant(scope), nameof(scope));
			}

			var lockFilePath = GetStatusFilePath(scope) + ".lock";

			ILockFile lockFile;
			if (!_lockFileManager.TryAcquireLock(lockFilePath, out lockFile))
			{
				Logger.Information("Could not build index, because it is already in use."); // TODO: Loc
			}

			using (lockFile)
			{
				// TODO: progress, cancellation, set status, proper error handling

				var provider = _indexManager.GetIndexProvider();
				var store = provider.GetIndexStore(scope);
				var info = GetIndexInfo(scope);
				var startedOnUtc = DateTime.UtcNow;

				info.Status = rebuild ? IndexingStatus.Rebuilding : IndexingStatus.Updating;
				SaveStatusFile(info);

				try
				{
					if (store.Exists && rebuild)
						store.Delete();
					
					store.CreateIfNotExists();

					DateTime? lastIndexedUtc = rebuild
						? null
						: info.LastIndexedUtc;

					var segmenter = collector.Collect(lastIndexedUtc, (i) => provider.CreateDocument(i));

					while (segmenter.ReadNextSegment())
					{
						var segment = segmenter.CurrentSegment;

						if (!rebuild)
						{
							var toDelete = segment.Where(x => x.OperationType == IndexOperationType.Delete).Select(x => x.Document.Id);
							store.DeleteDocuments(toDelete);
						}

						var toIndex = segment.Where(x => x.OperationType == IndexOperationType.Index).Select(x => x.Document);
						store.SaveDocuments(toIndex);
					}
				}
				finally
				{
					info.Status = IndexingStatus.Idle;
					info.LastIndexedUtc = startedOnUtc;
					SaveStatusFile(info);
				}
			}
		}

		public IndexInfo GetIndexInfo(string scope)
		{
			Guard.NotEmpty(scope, nameof(scope));

			var provider = _indexManager.GetIndexProvider();
			if (provider == null)
				return null;

			var store = provider.GetIndexStore(scope);
			var info = ReadStatusFile(store);

			info.Scope = scope;
			info.DocumentCount = store.DocumentCount;
			info.Fields = store.GetAllFields();

			return info;
		}

		private IndexInfo ReadStatusFile(IIndexStore store, string path = null)
		{
			var info = new IndexInfo();

			var folder = _env.AppDataFolder;
			path = path ?? GetStatusFilePath(store.Scope);

			if (folder.FileExists(path))
			{
				info = IndexInfo.FromXml(folder.ReadFile(path));
			}

			if (!store.Exists)
			{
				info.Status = IndexingStatus.Unavailable;
			}

			return info;
		}

		private void SaveStatusFile(IndexInfo info, string path = null)
		{
			path = path ?? GetStatusFilePath(info.Scope);
			var xml = info.ToXml();		

			_env.AppDataFolder.CreateTextFile(path, xml);
		}

		private string GetStatusFilePath(string scope)
		{
			var fileName = SeoHelper.GetSeName("{0}-{1}.xml".FormatInvariant(scope, _env.EnvironmentIdentifier), false, false);
			return _env.AppDataFolder.Combine("Indexing", fileName);
		}

		private IIndexCollector GetCollectorFor(string scope)
		{
			Guard.NotEmpty(scope, nameof(scope));

			return _collectors.FirstOrDefault(x => x.Scope.IsCaseInsensitiveEqual(scope));
		}
	}
}
