using UnityEngine;

public class HelpPanelController : MonoBehaviour
{
    [Header("Assigned in Inspector")]
    public GameObject helpPanel;  // drag your HelpPanel here

    // Show panel
    public void Show()
    {
        if (helpPanel != null) helpPanel.SetActive(true);
    }

    // Hide panel
    public void Hide()
    {
        if (helpPanel != null) helpPanel.SetActive(false);
    }

    // Toggle panel on/off (optional)
    public void Toggle()
    {
        if (helpPanel != null)
        {
            helpPanel.SetActive(!helpPanel.activeSelf);
        }
    }
}
