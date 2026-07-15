using System.Collections;

namespace HarnessAgentDiagnostics.Tests;

internal sealed class ThrowingList<T> : IList<T>
{
    public bool Accessed { get; private set; }

    public bool EnumeratorInvoked { get; private set; }

    public T this[int index]
    {
        get => throw Access();
        set => throw Access();
    }

    public int Count => throw Access();

    public bool IsReadOnly => true;

    public IEnumerator<T> GetEnumerator()
    {
        EnumeratorInvoked = true;
        throw Access();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Add(T item) => throw Access();

    public void Clear() => throw Access();

    public bool Contains(T item) => throw Access();

    public void CopyTo(T[] array, int arrayIndex) => throw Access();

    public int IndexOf(T item) => throw Access();

    public void Insert(int index, T item) => throw Access();

    public bool Remove(T item) => throw Access();

    public void RemoveAt(int index) => throw Access();

    private InvalidOperationException Access()
    {
        Accessed = true;
        return new InvalidOperationException("must not access custom lists");
    }
}

internal sealed class ThrowingDictionary<TKey, TValue> : IDictionary<TKey, TValue>
    where TKey : notnull
{
    public bool Accessed { get; private set; }

    public TValue this[TKey key]
    {
        get => throw Access();
        set => throw Access();
    }

    public ICollection<TKey> Keys => throw Access();

    public ICollection<TValue> Values => throw Access();

    public int Count => throw Access();

    public bool IsReadOnly => true;

    public void Add(TKey key, TValue value) => throw Access();

    public void Add(KeyValuePair<TKey, TValue> item) => throw Access();

    public void Clear() => throw Access();

    public bool Contains(KeyValuePair<TKey, TValue> item) => throw Access();

    public bool ContainsKey(TKey key) => throw Access();

    public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex) => throw Access();

    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => throw Access();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool Remove(TKey key) => throw Access();

    public bool Remove(KeyValuePair<TKey, TValue> item) => throw Access();

    public bool TryGetValue(TKey key, out TValue value)
    {
        value = default!;
        throw Access();
    }

    private InvalidOperationException Access()
    {
        Accessed = true;
        return new InvalidOperationException("must not access custom dictionaries");
    }
}

internal sealed class ThrowingReadOnlyList<T> : IReadOnlyList<T>
{
    public bool Accessed { get; private set; }

    public T this[int index] => throw Access();

    public int Count => throw Access();

    public IEnumerator<T> GetEnumerator() => throw Access();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private InvalidOperationException Access()
    {
        Accessed = true;
        return new InvalidOperationException("must not access custom lists");
    }
}

internal sealed class ThrowingReadOnlyDictionary<TKey, TValue> : IReadOnlyDictionary<TKey, TValue>
    where TKey : notnull
{
    public bool Accessed { get; private set; }

    public TValue this[TKey key] => throw Access();

    public IEnumerable<TKey> Keys => throw Access();

    public IEnumerable<TValue> Values => throw Access();

    public int Count => throw Access();

    public bool ContainsKey(TKey key) => throw Access();

    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => throw Access();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool TryGetValue(TKey key, out TValue value)
    {
        value = default!;
        throw Access();
    }

    private InvalidOperationException Access()
    {
        Accessed = true;
        return new InvalidOperationException("must not access custom dictionaries");
    }
}
