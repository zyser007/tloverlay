namespace TLOverlay.Core.Translation;

public interface ITranslationCache
{
    bool TryGet(string key, out string value);

    void Set(string key, string value);
}

/// <summary>
/// Bounded in-memory LRU. First line of defence, and the only one on the hot
/// path when a visual novel loops the same dialogue.
/// </summary>
public sealed class MemoryTranslationCache : ITranslationCache
{
    private readonly int _capacity;
    private readonly Dictionary<string, LinkedListNode<KeyValuePair<string, string>>> _index;
    private readonly LinkedList<KeyValuePair<string, string>> _order = new();
    private readonly object _gate = new();

    public MemoryTranslationCache(int capacity = 2048)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
        _index = new Dictionary<string, LinkedListNode<KeyValuePair<string, string>>>(capacity, StringComparer.Ordinal);
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _index.Count;
            }
        }
    }

    public bool TryGet(string key, out string value)
    {
        ArgumentNullException.ThrowIfNull(key);

        lock (_gate)
        {
            if (!_index.TryGetValue(key, out var node))
            {
                value = string.Empty;
                return false;
            }

            _order.Remove(node);
            _order.AddFirst(node);
            value = node.Value.Value;
            return true;
        }
    }

    public void Set(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        lock (_gate)
        {
            if (_index.TryGetValue(key, out var existing))
            {
                _order.Remove(existing);
                _index.Remove(key);
            }

            var node = _order.AddFirst(new KeyValuePair<string, string>(key, value));
            _index[key] = node;

            while (_index.Count > _capacity)
            {
                var oldest = _order.Last;
                if (oldest is null)
                {
                    break;
                }

                _order.RemoveLast();
                _index.Remove(oldest.Value.Key);
            }
        }
    }
}

/// <summary>
/// Reads through a fast cache to a slower persistent one, and writes to both.
/// </summary>
public sealed class LayeredTranslationCache : ITranslationCache
{
    private readonly ITranslationCache _fast;
    private readonly ITranslationCache _slow;

    public LayeredTranslationCache(ITranslationCache fast, ITranslationCache slow)
    {
        _fast = fast ?? throw new ArgumentNullException(nameof(fast));
        _slow = slow ?? throw new ArgumentNullException(nameof(slow));
    }

    public bool TryGet(string key, out string value)
    {
        if (_fast.TryGet(key, out value))
        {
            return true;
        }

        if (_slow.TryGet(key, out value))
        {
            // Promote so the next hit stays off disk.
            _fast.Set(key, value);
            return true;
        }

        return false;
    }

    public void Set(string key, string value)
    {
        _fast.Set(key, value);
        _slow.Set(key, value);
    }
}
