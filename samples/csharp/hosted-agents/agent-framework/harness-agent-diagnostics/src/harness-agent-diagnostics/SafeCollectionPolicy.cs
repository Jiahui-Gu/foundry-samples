using System.Collections;
using System.Collections.ObjectModel;
using System.Reflection;

namespace HarnessAgentDiagnostics;

internal static class SafeCollectionPolicy
{
    internal const int MaximumElements = 100;

    internal static bool IsSafeSequence(object value, int wrapperDepth = 0)
    {
        Type type = value.GetType();
        if (type.IsArray
            || IsExactGenericDefinition(type, typeof(List<>))
            || IsExactGenericDefinition(type, typeof(Collection<>)))
        {
            return true;
        }

        return wrapperDepth < 8
            && IsExactGenericDefinition(type, typeof(ReadOnlyCollection<>))
            && TryGetBackingCollection(value, "list", out object? backing)
            && IsSafeSequence(backing, wrapperDepth + 1);
    }

    internal static bool IsSafeDictionary(object value, int wrapperDepth = 0)
    {
        Type type = value.GetType();
        if (value is not IDictionary)
        {
            return false;
        }

        if (IsExactGenericDefinition(type, typeof(Dictionary<,>))
            || IsExactGenericDefinition(type, typeof(SortedDictionary<,>))
            || IsExactGenericDefinition(type, typeof(SortedList<,>)))
        {
            return true;
        }

        return wrapperDepth < 8
            && IsExactGenericDefinition(type, typeof(ReadOnlyDictionary<,>))
            && TryGetBackingCollection(value, "m_dictionary", out object? backing)
            && IsSafeDictionary(backing, wrapperDepth + 1);
    }

    private static bool IsExactGenericDefinition(Type type, Type definition)
        => type.IsGenericType && type.GetGenericTypeDefinition() == definition;

    private static bool TryGetBackingCollection(object wrapper, string fieldName, out object backing)
    {
        FieldInfo? field = wrapper.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        backing = field?.GetValue(wrapper)!;
        return backing is not null;
    }

}
