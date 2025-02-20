using System;
using System.Linq;
using LibGit2Sharp;
using Tfvc2Git.Core.ActionHandlers;
using Tfvc2Git.Core.Configuration;
using Tfvc2Git.Core.Configuration.Options;
using Tfvc2Git.Core.Extensions;
using Tfvc2Git.Core.Models;
using Tfvc2Git.Core.Repositories;
using Tfvc2Git.Core.RunHandlers.Base;

namespace Tfvc2Git.Core.RunHandlers
{
	public sealed class ConvertRunHandler : RunHandlerBase<ConvertOptions>
    {
        #region Fields
        private readonly Tfvc2GitRepository _repository;
        #endregion

        public ConvertRunHandler(Tfvc2GitRepository repository, ConfigBuilder configBuilder, Tfvc2GitConfig config)
            : base(configBuilder, config)
        {
            _repository = repository;
        }

        protected override void PreProcess()
        {
            base.PreProcess();
            var firstHistoryEntry = Config.History.FirstOrDefault();
            var firstBranch = Config.Branches.FirstOrDefault();
            Guard.IsNotNull(firstHistoryEntry, "No history found!");
            Guard.IsNotNull(firstBranch, "No configured branch!");
            //Guard.IsFalse(firstHistoryEntry?.Branch != firstBranch && Config.Branches.Select(x => x.GitBranchName).Distinct().Count() > 1,
            //    "First ChangesetId should come from the 'main' branch!");
        }

        protected override void Process()
        {
            var historyLength = Config.History.Count;
            var currentServerPath = string.Empty;
            var isFirstCommitDone = false;
            var changesDone = 0;
            for (var i = 0; i < historyLength; i++)
            {
                var historyEntry = Config.History[i];
                var branchMap = historyEntry.Branch;

                if (historyEntry.Committed)
                {
                    historyEntry.LogMigrated(i, historyLength);
                    if (historyEntry.IsFirstCommit)
                    {
                        isFirstCommitDone = true;
                    }

                    continue;
                }

                historyEntry.LogStartProcess(i, historyLength);
                currentServerPath = GetCurrentServerPath(historyEntry, currentServerPath);

                _repository.Clean();

                var fileChanges = _repository.Tfvc.GetChanges(historyEntry.ChangesetId, branchMap.TfvcServerPath, !historyEntry.IgnoreBranchesFilter);
                if (!fileChanges.Any())
                {
                    historyEntry
                        .WithActionTag("ignored:nochanges")
                        .PersistInNote(_repository)
                        .LogSkipped()
                        .LogEndProcess(i, historyLength);

                    changesDone++;
                    continue;
                }

                var relativePaths = fileChanges
                    .Select(x => x.Item.GetRelativePath(historyEntry.Branch.TfvcServerPath))
                    .ToArray();
                var notIgnoredChanges = relativePaths.Where(x => !_repository.Git.Ignore.IsPathIgnored(x)).ToArray();
                if (!notIgnoredChanges.Any())
                {
                    historyEntry
                        .WithActionTag("ignored:nochanges:all_in_gitignore")
                        .PersistInNote(_repository)
                        .LogSkipped()
                        .LogEndProcess(i, historyLength);

                    changesDone++;
                    continue;
                }

                if (!isFirstCommitDone)
                {
                    using (var firstCommit = new FirstCommitHandler())
                    {
                        firstCommit.Handle(_repository, branchMap, historyEntry);
                        firstCommit.FixIntegrity(_repository, branchMap, historyEntry.ChangesetId);

                        historyEntry.Author = _repository.Tfvc.GetAuthor(historyEntry.Changeset);
                        var commit = _repository
                            .Stage()
                            .Commit(historyEntry);

                        historyEntry
                            .WithSha(commit.Sha)
                            .WithActionTag("first_commit")
                            .SetAsFirstCommit()
                            .PersistInNote(_repository)
                            .LogEndProcess(i, historyLength);

                        isFirstCommitDone = true;
                        changesDone++;
                        continue;
                    }
                }

                var branch = _repository.Git.Branches[branchMap.GitBranchName];
                if (null == branch)
                {
                    if (branchMap.InitFromFirstMainCommit)
                    {
                        throw new NotImplementedException(nameof(branchMap.InitFromFirstMainCommit));
                    }

                    if (branchMap.InitFromChangesetId.HasValue)
                    {
                        throw new NotImplementedException(nameof(branchMap.InitFromChangesetId));
                    }

                    using (var branching = new BranchingHandler())
                    {
                        branching.Handle(_repository, branchMap, historyEntry);
                        branching.FixIntegrity(_repository, branchMap, historyEntry.ChangesetId);
                        historyEntry.Author = _repository.Tfvc.GetAuthor(historyEntry.Changeset);

                        var commit = _repository
                            .Stage()
                            .Commit(historyEntry);

                        if (null == commit)
                        {
                            Log.Warning(" - Empty commit");
                            commit = _repository
                                .Stage()
                                .Commit(historyEntry, true);
                        }

                        historyEntry
                            .WithSha(commit.Sha)
                            .WithActionTag("first_commit")
                            .SetAsFirstCommit()
                            .PersistInNote(_repository)
                            .LogEndProcess(i, historyLength);
                    }

                    changesDone++;
                    continue;
                }

                if (!branch.IsCurrentRepositoryHead || !_repository.Git.Head.FriendlyName.Equals(branch.FriendlyName))
                {
                    _repository.Clean();
                    _repository.Checkout(branch.FriendlyName);
                }

                switch (fileChanges.Any(x => x.ChangeType.HasMerge()))
                {
                    case true when Config.Branches.Count == 1:
                        Log.Warning(" - Merge checkin with Changeset '{changesetId}' will be treated as checkin.", historyEntry.ChangesetId);
                        break;
                    case true:
                    {
                        var mergedSources = fileChanges
                            .SelectMany(x => x.MergeSources)
                            .Where(x => x.VersionTo < historyEntry.ChangesetId)
                            .ToArray();
                        var parentChangesetId = mergedSources.Any() ? mergedSources.Max(x => x.VersionTo) : -1;
                        var parentHistoryEntries = Config.History
                                .Where(x => (x.Branch != branchMap) && (x.ChangesetId == parentChangesetId));
                        if (parentHistoryEntries.Count() > 1)
                        {
                                Log.Warning("More than one parent history entries found.");
                        }
                        var parentHistoryEntry = parentHistoryEntries.FirstOrDefault(he => !string.IsNullOrWhiteSpace(he.GitSha));

                        if (!string.IsNullOrWhiteSpace(parentHistoryEntry?.GitSha))
                        {
                            using (var merge = new MergeHandler())
                            {
                                merge.Handle(_repository, branchMap, historyEntry, parentHistoryEntry);
                                merge.FixIntegrity(_repository, branchMap, historyEntry.ChangesetId);

                                var commit = _repository
                                    .Stage()
                                    .Commit(historyEntry, true);

                                var s = _repository.Git.RetrieveStatus();

                                historyEntry
                                    .WithSha(commit.Sha)
                                    .WithActionTag($"merged:{historyEntry.ChangesetId}:{historyEntry.ParentChangesetId}")
                                    .PersistInNote(_repository);
                            }

                            changesDone++;
                            continue;
                        }

                        Log.Warning(" - Parent Changeset '{parentChangesetId}' not found in configured branches.", parentChangesetId);
                        Log.Warning(" - Merge checkin with Changeset '{changesetId}' will be treated as checkin.", historyEntry.ChangesetId);
                        break;
                    }
                }

                if (fileChanges.Any(x => x.ChangeType.HasBranch()))
                {
                    if (fileChanges.Any(x => !x.ChangeType.HasBranch()))
                    {
                        Log.Warning(" - Branching of individual files within a repo is not supported in git - will be treated as new checkin.");
                    }
                    else
                    {
                        Log.Warning(" - Branching from outside configured branches - will be treated as checkin.");
                    }
                }

                if (_repository.Git.RetrieveStatus().IsDirty)
                    _repository.Clean();

                using (var changesetToCommit = new ChangesetToCommitHandler())
                {
                    var transformResult = changesetToCommit.Transform(_repository, historyEntry, fileChanges, branchMap);
                    changesetToCommit.FixIntegrity(_repository, branchMap, historyEntry.ChangesetId);

                    var gitStatus = _repository.Git.RetrieveStatus();
                    if (!gitStatus.IsDirty)
                    {
                        Log.Warning(" - No changes left after cleaning, record as 'ignored:nochanges:!IsDirty'");
                        transformResult = ChangesetToCommitHandler.TransformResult.Nochanges;
                    }

                    switch (transformResult)
                    {
                        case ChangesetToCommitHandler.TransformResult.Success:

                            var changeset = branchMap.TfvcHistory.Single(x => x.ChangesetId == historyEntry.ChangesetId);

                            historyEntry.Author = _repository.Tfvc.GetAuthor(changeset);

                            var commit = _repository
                                .Stage()
                                .Commit(historyEntry);

                            if (null == commit)
                            {
                                Log.Warning(" - Empty commit");
                                commit = _repository
                                    .Stage()
                                    .Commit(historyEntry, true);
                            }

                            historyEntry
                                .WithSha(commit.Sha)
                                .WithActionTag("migrated")
                                .PersistInNote(_repository)
                                .LogEndProcess(i, historyLength);

                            break;
                        case ChangesetToCommitHandler.TransformResult.Nochanges:
                            historyEntry
                                .WithActionTag("ignored:nochanges:!IsDirty")
                                .PersistInNote(_repository)
                                .LogEndProcess(i, historyLength);

                            break;
                        case ChangesetToCommitHandler.TransformResult.Fail:
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }

                changesDone++;
            }
        }

        private string GetCurrentServerPath(HistoryEntry historyEntry, string lastValue)
        {
            if (historyEntry.Branch.TfvcServerPath != lastValue && !string.IsNullOrWhiteSpace(lastValue))
            {
                Log.Information(" - Switching branch '{From}' to '{To}'", lastValue, historyEntry.Branch.TfvcServerPath);
            }

            return historyEntry.Branch.TfvcServerPath;
        }
    }
}