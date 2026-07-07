public class EmergencyKillSwitch
{
    public bool IsActive { get; private set; }
    public string ActivatedBy { get; private set; } = "";

    public void Activate(string admin)
    {
        IsActive = true;
        ActivatedBy = admin;
    }

    public void Deactivate(string admin)
    {
        IsActive = false;
    }
}
