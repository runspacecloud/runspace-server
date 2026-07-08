public static class ServerApi
{
    public static void Register(WebApplication app)
    {
        ServerDb.EnsureSchema();
        ServerCrud.Register(app);
        ServerMembers.Register(app);
        ServerRoles.Register(app);
        ServerInvites.Register(app);
        ServerChannels.Register(app);
        ServerMessages.Register(app);
        ServerBans.Register(app);
    }
}
