using System.Collections.Generic;
using System.Linq;
using Tfvc2Git.Core.Models;

namespace Tfvc2Git.Core.Configuration
{
    public sealed partial class ConfigBuilder
    {
        private ConfigBuilder InitChangesets()
        {
            if (!_config.UseTfvc || !_config.InitChangeSets)
                return this;

            _log.Information("{Step}", nameof(InitChangesets));
            var changesetIds = new List<int>();
            foreach (var branchMap in _config.Branches)
            {
                var history = _repository.Tfvc.GetHistory(branchMap.TfvcServerPath);
                if (_config.FromChangesetId != -1)
                {
                    var filteredHistory = history.Where(x => x.ChangesetId >= _config.FromChangesetId).ToArray();
                    _log.Information(" - Filter history from changeset {ChangesetId} : {FilteredHistoryCount}/{HistoryCount}",
                        _config.FromChangesetId, filteredHistory.Length, history.Length);
                    history = filteredHistory;
                }

                if (_config.ToChangesetId != -1)
                {
                    var filteredHistory = history.Where(x => x.ChangesetId <= _config.ToChangesetId).ToArray();
                    _log.Information(" - Filter history up to changeset {ChangesetId} : {FilteredHistoryCount}/{HistoryCount}",
                        _config.ToChangesetId, filteredHistory.Length, history.Length);
                    history = filteredHistory;
                }

                branchMap.TfvcHistory = history;
                changesetIds.AddRange(branchMap.TfvcHistory.Select(x => x.ChangesetId));
            }

            changesetIds = changesetIds.Distinct().OrderBy(x => x).ToList();
            _config.History = new List<HistoryEntry>(changesetIds.Count);
            foreach (var changesetId in changesetIds)
            {
                var date = _config.Branches
                    .SelectMany(x => x.TfvcHistory)
                    .First(x => x.ChangesetId == changesetId)
                    .CreationDate;

                var branches = _config.Branches
                    .Where(x => x.TfvcHistory.Any(xx => xx.ChangesetId == changesetId)).ToArray();
                var isSplittedChangeset = branches.Length > 1;
                var ignoreBranchesFilter = false;

                if (isSplittedChangeset && _config.Branches.Select(x => x.GitBranchName).Distinct().Count() == 1)
                {
                    var removedBranches = branches.Skip(1).ToArray();
                    branches = branches.Take(1).ToArray();
                    foreach (var removedBranch in removedBranches)
                    {
                        _log.Warning(" - Changeset '{ChangesetId}' will be ignored for branch map '{BranchMap}'", changesetId, removedBranch);
                    }

                    ignoreBranchesFilter = true;
                }

                foreach (var branch in branches)
                {
                    var changeset = branch.TfvcHistory.Single(x => x.ChangesetId == changesetId);
                    string message = changeset.Comment ?? string.Empty;

                    if (changeset.WorkItems.Any())
                    {
                        message += "\r\n\r\nRefs: #" + string.Join(", #", changeset.WorkItems.Select(wi => wi.Id));
                    }

					_config.History.Add(new HistoryEntry
                    {
                        ChangesetId = changesetId,
                        Message = message,
                        Date = date,
                        Branch = branch,
                        Changeset = changeset,
                        IsSplittedChangeset = isSplittedChangeset,
                        IgnoreBranchesFilter = ignoreBranchesFilter
                    });
                }
            }

            return this;
        }
    }
}