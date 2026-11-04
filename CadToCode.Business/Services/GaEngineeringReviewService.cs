using System.Collections.Concurrent;
using CadToCode.Models.Dtos;

namespace CadToCode.Business.Services
{
    public sealed class GaEngineeringReviewService
    {
        private readonly ConcurrentDictionary<int, GaReviewState> _states = new();
        private readonly object _gate = new();

        public GaReviewState GetState(int projectId)
        {
            lock (_gate)
            {
                return Clone(_states.GetOrAdd(projectId, id => new GaReviewState { ProjectId = id }));
            }
        }

        /// <summary>
        /// Stores the latest suggestions. A decision already made for the same part and field is kept
        /// when the suggestion has not changed.
        /// </summary>
        public GaReviewState Submit(int projectId, IEnumerable<GaReviewItem> items)
        {
            ArgumentNullException.ThrowIfNull(items);

            lock (_gate)
            {
                var previous = _states.TryGetValue(projectId, out var existing)
                    ? existing.Items
                    : new List<GaReviewItem>();

                var merged = new List<GaReviewItem>();

                foreach (var item in items)
                {
                    var old = previous.FirstOrDefault(p =>
                        string.Equals(p.PartName, item.PartName, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(p.Field, item.Field, StringComparison.OrdinalIgnoreCase));

                    if (old is not null
                        && old.Status != GaReviewStatus.Pending
                        && string.Equals(old.SuggestedValue, item.SuggestedValue, StringComparison.Ordinal))
                    {
                        item.Id = old.Id;
                        item.Status = old.Status;
                        item.ApprovedValue = old.ApprovedValue;
                    }
                    else if (old is not null && old.Status == GaReviewStatus.Approved
                        && !string.IsNullOrWhiteSpace(old.ApprovedValue))
                    {
                        item.Id = old.Id;
                        item.Status = old.Status;
                        item.ApprovedValue = old.ApprovedValue;
                    }

                    merged.Add(item);
                }

                // An approved value is applied to the callout before the next run, so the run no longer
                // reports it as missing. Keep it here so the approval is not lost.
                foreach (var old in previous.Where(p => p.Status == GaReviewStatus.Approved))
                {
                    var present = merged.Any(m =>
                        string.Equals(m.PartName, old.PartName, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(m.Field, old.Field, StringComparison.OrdinalIgnoreCase));

                    if (!present)
                    {
                        merged.Add(old);
                    }
                }

                var state = new GaReviewState { ProjectId = projectId, Items = merged };
                _states[projectId] = state;
                return Clone(state);
            }
        }

        public bool Approve(int projectId, string itemId, string? overrideValue)
        {
            lock (_gate)
            {
                var item = Find(projectId, itemId);
                if (item is null)
                {
                    return false;
                }

                var value = string.IsNullOrWhiteSpace(overrideValue) ? item.SuggestedValue : overrideValue.Trim();
                if (string.IsNullOrWhiteSpace(value))
                {
                    return false;
                }

                item.ApprovedValue = value;
                item.Status = GaReviewStatus.Approved;
                return true;
            }
        }

        public bool Reject(int projectId, string itemId)
        {
            lock (_gate)
            {
                var item = Find(projectId, itemId);
                if (item is null)
                {
                    return false;
                }

                item.ApprovedValue = null;
                item.Status = GaReviewStatus.Rejected;
                return true;
            }
        }

        public bool TryGetApproved(int projectId, string partName, string field, out string value)
        {
            lock (_gate)
            {
                value = string.Empty;

                if (!_states.TryGetValue(projectId, out var state))
                {
                    return false;
                }

                var item = state.Items.FirstOrDefault(i =>
                    i.Status == GaReviewStatus.Approved
                    && !string.IsNullOrWhiteSpace(i.ApprovedValue)
                    && string.Equals(i.PartName, partName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(i.Field, field, StringComparison.OrdinalIgnoreCase));

                if (item is null)
                {
                    return false;
                }

                value = item.ApprovedValue!;
                return true;
            }
        }

        /// <summary>Parts the user rejected a required item for. They are left out of the package.</summary>
        public List<string> ExcludedParts(int projectId)
        {
            lock (_gate)
            {
                if (!_states.TryGetValue(projectId, out var state))
                {
                    return new List<string>();
                }

                return state.Items
                    .Where(i => i.Required && i.Status == GaReviewStatus.Rejected)
                    .Select(i => i.PartName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        public bool HasPendingRequired(int projectId)
        {
            lock (_gate)
            {
                return _states.TryGetValue(projectId, out var state) && !state.AllRequiredResolved;
            }
        }

        public List<string> PendingRequiredMessages(int projectId)
        {
            lock (_gate)
            {
                if (!_states.TryGetValue(projectId, out var state))
                {
                    return new List<string>();
                }

                return state.Items
                    .Where(i => i.Required && i.Status == GaReviewStatus.Pending)
                    .Select(i => i.PartName + ": " + i.Field + " not approved"
                        + (string.IsNullOrWhiteSpace(i.SuggestedValue) ? "." : " (suggested " + i.SuggestedValue + ")."))
                    .ToList();
            }
        }

        private GaReviewItem? Find(int projectId, string itemId)
        {
            return _states.TryGetValue(projectId, out var state)
                ? state.Items.FirstOrDefault(i => string.Equals(i.Id, itemId, StringComparison.OrdinalIgnoreCase))
                : null;
        }

        private static GaReviewState Clone(GaReviewState source)
        {
            return new GaReviewState
            {
                ProjectId = source.ProjectId,
                Items = source.Items.Select(i => new GaReviewItem
                {
                    Id = i.Id,
                    PartName = i.PartName,
                    Field = i.Field,
                    CurrentValue = i.CurrentValue,
                    SuggestedValue = i.SuggestedValue,
                    ApprovedValue = i.ApprovedValue,
                    Source = i.Source,
                    Required = i.Required,
                    Status = i.Status,
                    Reason = i.Reason
                }).ToList()
            };
        }
    }
}
