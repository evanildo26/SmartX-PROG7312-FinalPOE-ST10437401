using SmartX.Shared;

namespace SmartX.Api.Services;

/// <summary>
/// Something an operator did: a manual command (CMD) or a device search (SEARCH).
/// A readonly record struct so it can be used as a Dictionary key with value equality.
/// </summary>
public readonly record struct OperatorAction(string Kind, string MacAddress, bool? State)
{
    public static OperatorAction Command(string mac, bool on) => new("CMD", mac, on);
    public static OperatorAction Search(string mac) => new("SEARCH", mac, null);
    public bool IsCommand => Kind == "CMD";
    public override string ToString() => IsCommand ? $"CMD:{MacAddress}:{(State == true ? "ON" : "OFF")}" : $"SEARCH:{MacAddress}";
}

/// <summary>
/// Automated Action Engine (predictive recommendations).
///
/// It learns from operator behaviour in two ways:
///  1. Trigger → Action association: when an alert fires (e.g. MOISTURE_LOW on
///     "Moisture Sensor B") every command/search the operator performs inside
///     the following time window is credited to that trigger. Over time this
///     gives P(action | trigger) = count(trigger→action) / occurrences(trigger).
///  2. Action → Next Action (first-order Markov chain): "operators who searched
///     Moisture Sensor B usually searched Pump A next".
///
/// Data structures:
///  - Dictionary&lt;string, Dictionary&lt;OperatorAction,int&gt;&gt;  trigger → action counts (O(1) update/lookup)
///  - Dictionary&lt;string,int&gt;                                  trigger occurrence counts
///  - Dictionary&lt;OperatorAction, Dictionary&lt;OperatorAction,int&gt;&gt; Markov transition table
///  - Queue&lt;RecentTrigger&gt;  sliding time window of recent triggers (FIFO expiry)
///  - HashSet&lt;OperatorAction&gt; per trigger so one action is credited once per occurrence
///
/// Not thread-safe on its own: CommandStreamEngine calls it while holding its lock.
/// </summary>
public sealed class ActionPatternEngine
{
    private sealed class RecentTrigger
    {
        public required string Key { get; init; }
        public required DateTime At { get; init; }
        public HashSet<OperatorAction> Credited { get; } = new();
    }

    public TimeSpan CreditWindow { get; init; } = TimeSpan.FromMinutes(3);
    public TimeSpan MarkovWindow { get; init; } = TimeSpan.FromMinutes(10);
    public int MinimumSupport { get; init; } = 2;

    private readonly Dictionary<string, Dictionary<OperatorAction, int>> _triggerToAction = new();
    private readonly Dictionary<string, int> _triggerOccurrences = new();
    private readonly Dictionary<OperatorAction, Dictionary<OperatorAction, int>> _transitions = new();
    private readonly Dictionary<OperatorAction, int> _actionFrequency = new();
    private readonly Queue<RecentTrigger> _recentTriggers = new();

    private OperatorAction? _lastAction;
    private DateTime _lastActionAt;

    public static string TriggerKey(string alertCode, string mac) => $"{alertCode}@{mac}";

    /// <summary>Called once when an alert is NEWLY raised (duplicates are filtered upstream by a HashSet).</summary>
    public void ObserveTrigger(string triggerKey, DateTime at)
    {
        _triggerOccurrences[triggerKey] = _triggerOccurrences.GetValueOrDefault(triggerKey) + 1;
        _recentTriggers.Enqueue(new RecentTrigger { Key = triggerKey, At = at });
        PruneTriggers(at);
    }

    /// <summary>Called whenever the operator issues a command or searches for a device.</summary>
    public void ObserveAction(OperatorAction action, DateTime at)
    {
        PruneTriggers(at);

        // 1. Credit this action to every trigger that fired within the window.
        foreach (var trigger in _recentTriggers)
        {
            if (at - trigger.At > CreditWindow || at < trigger.At) continue;
            if (!trigger.Credited.Add(action)) continue;          // already credited for this occurrence

            if (!_triggerToAction.TryGetValue(trigger.Key, out var counts))
                _triggerToAction[trigger.Key] = counts = new Dictionary<OperatorAction, int>();
            counts[action] = counts.GetValueOrDefault(action) + 1;
        }

        // 2. Markov chain: previous action → this action.
        if (_lastAction is { } prev && prev != action && at - _lastActionAt <= MarkovWindow)
        {
            if (!_transitions.TryGetValue(prev, out var next))
                _transitions[prev] = next = new Dictionary<OperatorAction, int>();
            next[action] = next.GetValueOrDefault(action) + 1;
        }

        _actionFrequency[action] = _actionFrequency.GetValueOrDefault(action) + 1;
        _lastAction = action;
        _lastActionAt = at;
    }

    /// <summary>Removes triggers older than the credit window from the front of the FIFO queue.</summary>
    private void PruneTriggers(DateTime now)
    {
        while (_recentTriggers.Count > 0 && now - _recentTriggers.Peek().At > CreditWindow)
            _recentTriggers.Dequeue();
    }

    /// <summary>Forget the "current session" context (used after seeding historical behaviour).</summary>
    public void ResetSessionContext()
    {
        _recentTriggers.Clear();
        _lastAction = null;
    }

    /// <summary>Learned actions for a trigger with at least MinimumSupport observations, best first.</summary>
    public IEnumerable<(OperatorAction Action, int Count, int Occurrences, double Confidence)> ActionsFor(string triggerKey)
    {
        if (!_triggerToAction.TryGetValue(triggerKey, out var counts)) yield break;
        int occurrences = Math.Max(1, _triggerOccurrences.GetValueOrDefault(triggerKey));
        foreach (var (action, count) in counts.OrderByDescending(kv => kv.Value))
        {
            if (count < MinimumSupport) continue;
            yield return (action, count, occurrences, Math.Min(1.0, (double)count / occurrences));
        }
    }

    /// <summary>Most likely next actions after the operator's most recent action (Markov chain).</summary>
    public IEnumerable<(OperatorAction Previous, OperatorAction Next, int Count, double Confidence)> NextActions(DateTime now)
    {
        if (_lastAction is not { } prev || now - _lastActionAt > MarkovWindow) yield break;
        if (!_transitions.TryGetValue(prev, out var next)) yield break;
        int total = next.Values.Sum();
        foreach (var (action, count) in next.OrderByDescending(kv => kv.Value))
        {
            if (count < MinimumSupport) continue;
            yield return (prev, action, count, (double)count / total);
        }
    }

    public int SearchFrequency(string mac) => _actionFrequency.GetValueOrDefault(OperatorAction.Search(mac));

    public List<(string Trigger, OperatorAction Action, int Count, int Occurrences, double Confidence)> AllPatterns()
    {
        var list = new List<(string, OperatorAction, int, int, double)>();
        foreach (var (trigger, counts) in _triggerToAction)
        {
            int occ = Math.Max(1, _triggerOccurrences.GetValueOrDefault(trigger));
            foreach (var (action, count) in counts)
                list.Add((trigger, action, count, occ, Math.Min(1.0, (double)count / occ)));
        }
        return list.OrderByDescending(p => p.Item5).ThenByDescending(p => p.Item3).ToList();
    }
}
