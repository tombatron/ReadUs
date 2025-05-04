using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ReadUs;

public class RedisClusterDatabase(RedisClusterConnectionPool pool) : RedisDatabase(pool)
{
    public override async Task<Result> SetMultipleAsync(KeyValuePair<RedisKey, string>[] keysAndValues,
        CancellationToken cancellationToken = default)
    {
        var keyGroups = keysAndValues.GroupBy(x => x.Key.Slot);

        // TODO: I don't like this. I think the default behavior should be to return an error if the keys
        //       don't belong to the same slot. Should we support setting keys on multiple slots? Sure I guess, 
        //       but if we do that, I think we could be a bit smarter about it than what we're doing here. 
        
        var setMultipleTasks = new List<Task<Result>>();

        foreach (var keyGroup in keyGroups)
        {
            setMultipleTasks.Add(base.SetMultipleAsync(keyGroup.ToArray(), cancellationToken));
        }

        await Task.WhenAll(setMultipleTasks);
        
        // I don't like this...
        foreach (var setTask in setMultipleTasks)
        {
            var setResult = await setTask;

            if (setResult is Ok)
            {
                // No - Op, dumb.
            }
            
            if (setResult is Error err)
            {
                return err;
            }
        }
        
        return Result.Ok;
    }
}