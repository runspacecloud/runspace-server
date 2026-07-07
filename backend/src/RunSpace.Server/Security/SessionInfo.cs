public class SessionInfo
{
    public string SessionId { get; set; } = "";
    public string Ip { get; set; } = "";
    public string UserAgent { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime LastActivity { get; set; }
}
