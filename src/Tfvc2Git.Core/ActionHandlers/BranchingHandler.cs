using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using LibGit2Sharp;
using Microsoft.TeamFoundation.VersionControl.Client;
using Microsoft.VisualStudio.Services.Common;
using Tfvc2Git.Core.Extensions;
using Tfvc2Git.Core.Models;
using Tfvc2Git.Core.Repositories;

namespace Tfvc2Git.Core.ActionHandlers
{
	public sealed class BranchingHandler : Tfvc2GitActionBase
    {
        public void Handle(Tfvc2GitRepository repository, BranchMap branchMap, HistoryEntry historyEntry, bool filterByHistory = true)
        {
            if (branchMap.InitFromFirstMainCommit)
            {
                Handle(repository, historyEntry, repository.InitialCommitSha);
            }
            else if (branchMap.InitFromChangesetId.HasValue)
            {
                HistoryEntry branchFrom = repository.Config.History.FirstOrDefault(he => he.ChangesetId == branchMap.InitFromChangesetId.Value);

                if (branchFrom != null)
                {
                    Handle(repository, historyEntry, branchFrom.ChangesetId, branchFrom.Branch.TfvcServerPath);
                }
                else
                {
                    Log.Fatal(" - InitFromChangesetId for branch {ServerPath} does not exist!", branchMap.TfvcServerPath);
                }
            }
            else
            {
                var branchFilter = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                branchFilter.AddRange(repository.Config.Branches.Select(x => x.TfvcServerPath));

                var parentRootChangesetId = -1;
                ItemIdentifier sourceItemIdentifier = null;

                ItemSpec[] itemSpecs = new ItemSpec[] { new ItemSpec(branchMap.TfvcServerPath, RecursionType.OneLevel) };
                BranchHistoryTreeItem[][] branchHistoryTrees = repository.Tfvc.Vcs.GetBranchHistory(itemSpecs, new ChangesetVersionSpec(historyEntry.ChangesetId));
                foreach (var branchHistoryTree in branchHistoryTrees)
                {
                    foreach (var item in branchHistoryTree)
                    {
                        FindParentBranch(branchMap, historyEntry, branchFilter, ref parentRootChangesetId, ref sourceItemIdentifier, item);

                        if (sourceItemIdentifier != null)
                        {
                            break;
                        }
                    }

                    if (sourceItemIdentifier != null)
                    {
                        break;
                    }
                }

                if (sourceItemIdentifier != null)
                {
                    var targetItemIdentifier = new[] { new ItemIdentifier(branchMap.TfvcServerPath) };
                    var allChangesetIds = repository.Config.History
                        .Where(x => x.ChangesetId < historyEntry.ChangesetId)
                        .Where(x => x.ChangesetId >= parentRootChangesetId)
                        .Select(x => x.ChangesetId)
                        .Distinct()
                        .ToArray();
                    var extendedMerges = new List<ExtendedMerge>();
                    var changesetIdsTemp = allChangesetIds.ToList();
                    const int chunkSize = 50;
                    while (changesetIdsTemp.Any())
                    {
                        var currentIds = changesetIdsTemp.Take(chunkSize).ToList();
                        var merges = repository.Tfvc.Vcs.TrackMerges(currentIds.ToArray(), sourceItemIdentifier, targetItemIdentifier, null);
                        extendedMerges.AddRange(merges);
                        changesetIdsTemp = changesetIdsTemp.Skip(chunkSize).ToList();
                    }

                    var mergeChangesetIds = extendedMerges
                        .Where(x => x.TargetChangeset.ChangesetId == historyEntry.ChangesetId)
                        .Select(x => x.SourceChangeset.ChangesetId)
                        .ToList();

                    Log.Debug(" - Found {mergeChangesetIdsCount} possibles source changesetIds", mergeChangesetIds.Count);
                    if (filterByHistory)
                    {
                        mergeChangesetIds = mergeChangesetIds
                            .Where(x => repository.Config.History.Any(xx => xx.ChangesetId == x))
                            .ToList();
                        Log.Debug(" - {mergeChangesetIdsCount} possibles source changesetIds left after filtering by know history", mergeChangesetIds.Count);
                    }

                    if (mergeChangesetIds.Any())
                    {
                        var rootChangeset = mergeChangesetIds.Max();
                        Handle(repository, historyEntry, rootChangeset, sourceItemIdentifier.Item);
                    }
                    else
                    {
                        Log.Warning(" - Unable to find a changeset, fallback to Initial commit {GitSha} as source", repository.InitialCommitSha);
                        Handle(repository, historyEntry, repository.InitialCommitSha);
                    }
                }
                else
                {
                    if (parentRootChangesetId > 0)
                    {
                        Log.Warning(" - Source branch is not one of the filtered branches, fallback to Initial commit {GitSha} as source", repository.InitialCommitSha);
                    }
                    else
                    {
                        Log.Error(" - No parent branch found for {ServerPath}!", branchMap.TfvcServerPath);
                    }
                    Handle(repository, historyEntry, repository.InitialCommitSha);
                }
            }
        }

        private static void FindParentBranch(BranchMap branchMap, HistoryEntry historyEntry, HashSet<string> branchFilter, ref int parentRootChangesetId, ref ItemIdentifier sourceItemIdentifier, BranchHistoryTreeItem item)
        {
            if ((item.Relative.BranchToItem.ServerItem == branchMap.TfvcServerPath) &&
                (item.Relative.BranchToItem.ChangesetId == historyEntry.ChangesetId) &&
                (item.Relative.BranchFromItem != null))
            {
                parentRootChangesetId = item.Relative.BranchFromItem.ChangesetId;
                if (branchFilter.Contains(item.Relative.BranchFromItem.ServerItem))
                {
                    sourceItemIdentifier = new ItemIdentifier(item.Relative.BranchFromItem.ServerItem, new ChangesetVersionSpec(parentRootChangesetId));
                }
            }

            if (sourceItemIdentifier == null)
            {
                foreach (var child in item.Children.OfType<BranchHistoryTreeItem>())
                {
                    FindParentBranch(branchMap, historyEntry, branchFilter, ref parentRootChangesetId, ref sourceItemIdentifier, child);

                    if (sourceItemIdentifier != null)
                    {
                        break;
                    }
                }
            }
        }

        private void Handle(Tfvc2GitRepository repository, HistoryEntry historyEntry, string sha)
        {
            Log.Information(" - Use commit {GitSha} as branch source.", sha);

            var commit = repository.Git.Lookup<Commit>(sha);
            Guard.IsNotNull(commit, $"Commit '{sha}' not found!");

            historyEntry
                .WithSha(sha)
                .WithActionTag($"branched:{historyEntry.ChangesetId}:{historyEntry.ParentChangesetId}");

            repository.Git.CreateBranch(historyEntry.Branch.GitBranchName, commit);
            repository.Checkout(historyEntry.Branch.GitBranchName);
        }

        private void Handle(Tfvc2GitRepository repository, HistoryEntry historyEntry, int rootChangeset, string serverPath)
        {
            Log.Information(" - Use ChangesetId {ChangesetId} from branch {ServerPath} as branch source.", rootChangeset, serverPath);
            var parentHistoryEntry = repository.Config.History.SingleOrDefault(x => x.ChangesetId == rootChangeset);
            if (null == parentHistoryEntry)
            {
                Log.Debug(" - ChangesetId {ChangesetId} not found in configured branches.");
                Debugger.Break();
                throw new ApplicationException("parent branch not found in configured branches!");
            }

            historyEntry.ParentChangesetId = parentHistoryEntry.ChangesetId;

            Handle(repository, historyEntry, parentHistoryEntry.GitSha);
        }
    }
}