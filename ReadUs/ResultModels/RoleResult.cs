using System;
using ReadUs.Parser;

namespace ReadUs.ResultModels;

internal static class Roles
{
    internal const string Primary = "master";
    internal const string Replica = "slave";
}

public abstract class RoleResult
{
    public static explicit operator RoleResult(ParseResult result)
    {
        // First we need to check if the result we're parsing is an array or not...
        if (result.TryToArray(out var resultArray))
        {
            // We've got an array, now time to check the first entry to see what kind of result we're 
            // dealing with. 
            RoleResult role = resultArray[0].ToString() switch
            {
                Roles.Primary => (PrimaryRoleResult)resultArray,
                Roles.Replica => (ReplicaRoleResult)resultArray,
                _ => throw new Exception("Not sure what happend here but here we are.")
            };

            return role;
        }
        else
        {
            // TODO: Throw a custom exception here. 
            throw new Exception("We expected a result that was a multi-bulk here."); // Or something.
        }
    }
}