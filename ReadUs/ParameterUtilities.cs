using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("ReadUs.Tests")]
namespace ReadUs
{
    internal static class ParameterUtilities
    {
        internal static object?[] CombineParameters(params object[] parameters) =>
            UnwindObjectArray(parameters).ToArray();

        internal static IEnumerable<object?> UnwindObjectArray(object[] objects)
        {
            foreach (var obj in objects)
            {
                if (obj.GetType().IsArray)
                {
                    if (obj is KeyValuePair<RedisKey, string>[] kvps)
                    {
                        foreach(var kvp in kvps)
                        {
                            yield return kvp.Key.Name;
                            yield return kvp.Value;
                        }

                        continue;
                    }

                    var objArray = obj as object[];

                    if (objArray is null)
                    {
                        yield return objArray;

                        continue;
                    }

                    foreach (var obj2 in UnwindObjectArray(objArray))
                    {
                        yield return obj2;
                    }
                }
                else
                {
                    yield return obj;
                }
            }
        }
    }
}
