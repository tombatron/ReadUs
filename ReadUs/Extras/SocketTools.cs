using System;
using System.Net;
using System.Net.Sockets;

namespace ReadUs.Extras;

public static class SocketTools
{
    public static bool IsSocketAvailable(IPAddress address, int port, int timeoutMilliseconds = 50)
    {
        try
        {
            using var client = new TcpClient();
            
            var result = client.BeginConnect(address, port, null, null);
                
            var success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(timeoutMilliseconds));

            if (!success)
            {
                return false;
            }
                
            client.EndConnect(result);

            return true;
        }
        catch
        {
            return false;
        }
    }
}