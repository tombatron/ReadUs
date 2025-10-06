using System.Net;
using ReadUs.Parser;

namespace ReadUs.ResultModels;

public class ClusterShardsNode
{
    public string? Id { get; }
    public int? Port { get; }
    public IPAddress? Ip { get; }
    public string? Endpoint { get; }
    public string? Role { get; }
    public int? ReplicationOffset { get; }
    public string? Health { get; }

    public ClusterShardsNode(ParseResult result)
    {
        if (result.TryToArray(out var resultsArray))
        {
            for (var i = 0; i < resultsArray.Length; i++)
            {
                var name = resultsArray[i].ToString(); // We expect the first item in the list to be the property name.
                var value = resultsArray[++i];
                
                switch (name)
                {
                    case "id":
                        Id = value.ToString();
                        break;
                    case "port":
                        Port = int.Parse(value.Value);
                        break;
                    case "ip":
                        Ip = IPAddress.Parse(value.Value);
                        break;
                    case "endpoint":
                        Endpoint = value.ToString();
                        break;
                    case "role":
                        Role = value.ToString();
                        break;
                    case "replication-offset":
                        ReplicationOffset = int.Parse(value.Value);
                        break;
                    case "health":
                        Health = value.ToString();
                        break;
                }
            }
        }
    }
}