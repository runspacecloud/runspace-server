public class SessionAnomalyDetector
{
    public int AnalyzeSession(SessionInfo session, string ip, string userAgent)
    {
        var score = 0;

        if (!string.IsNullOrWhiteSpace(session.Ip) && session.Ip != ip)
        {
            score += 30;

            var previousPrefix = session.Ip.Split('.').FirstOrDefault() ?? "";
            var currentPrefix = ip.Split('.').FirstOrDefault() ?? "";

            if (previousPrefix != currentPrefix)
                score += 20;
        }

        if (!string.IsNullOrWhiteSpace(session.UserAgent) && session.UserAgent != userAgent)
            score += 40;

        return Math.Min(score, 100);
    }
}
