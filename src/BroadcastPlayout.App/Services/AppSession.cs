namespace BroadcastPlayout.Services;
public static class AppSession
{
    public static SecurityPrincipal? Current { get; set; }
    public static bool IsAdmin => Current?.Role.Equals("ADMIN",StringComparison.OrdinalIgnoreCase)==true;
}
