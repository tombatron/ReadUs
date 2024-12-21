using System;
using System.Security.Cryptography;
using System.Text;

namespace ReadUs.Extras;

internal static class HashTools
{
    public static string CreateMd5Hash(string payload)
    {
        using var md5 = MD5.Create();

        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var hashedPayload = md5.ComputeHash(payloadBytes);

        return BitConverter.ToString(hashedPayload).Replace("-", string.Empty);
    }
}