using System.Net;
using AskVest.Modules.Identity.Domain.Enums;
using AskVest.Modules.Identity.Persistence;
using AskVest.Modules.Identity.Persistence.Entities;
using AskVest.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace AskVest.Modules.Identity.Services;

public static class PrivacyMasking
{
    public static string? MaskPhone(string? phone)
    {
        if (string.IsNullOrEmpty(phone))
        {
            return null;
        }

        return phone.Length <= 4 ? "***" : $"***{phone[^4..]}";
    }

    public static string? CoarsenIp(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip) || !IPAddress.TryParse(ip, out var address))
        {
            return ip;
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            bytes[3] = 0;
        }
        else
        {
            for (var i = 6; i < bytes.Length; i++)
            {
                bytes[i] = 0;
            }
        }

        return new IPAddress(bytes).ToString();
    }
}
