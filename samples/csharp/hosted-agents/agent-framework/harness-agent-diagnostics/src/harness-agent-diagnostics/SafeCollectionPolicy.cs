using System.Collections;
using System.Collections.ObjectModel;

namespace HarnessAgentDiagnostics;

internal static class SafeCollectionPolicy
{
    internal const int MaximumElements = 100;

    internal static bool IsSafeSequence(object value)
    {
        Type type = value.GetType();
        return type.IsArray
            || IsExactGenericDefinition(type, typeof(List<>))
            || IsExactGenericDefinition(type, typeof(Collection<>))
            || IsExactGenericDefinition(type, typeof(ReadOnlyCollection<>));
    }

    internal static bool IsSafeDictionary(object value)
    {
        Type type = value.GetType();
        return value is IDictionary
            && (IsExactGenericDefinition(type, typeof(Dictionary<,>))
                || IsExactGenericDefinition(type, typeof(SortedDictionary<,>))
                || IsExactGenericDefinition(type, typeof(SortedList<,>))
                || IsExactGenericDefinition(type, typeof(ReadOnlyDictionary<,>)));
    }

    private static bool IsExactGenericDefinition(Type type, Type definition)
        => type.IsGenericType && type.GetGenericTypeDefinition() == definition;
}
