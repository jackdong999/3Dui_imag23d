using UnityEngine;
using UnityEngine.UI;

public class PageController : MonoBehaviour
{
    public TMPro.TextMeshProUGUI contentText;            // 显示内容的 UI Text
    public Button nextButton;
    public Button prevButton;

    [TextArea(3, 10)]
    public string[] pages;              // 每页的内容（可以改成图片或其他类型）

    private int currentPage = 0;

    void Start()
    {
        UpdatePage();
        nextButton.onClick.AddListener(NextPage);
        prevButton.onClick.AddListener(PrevPage);
    }

    void UpdatePage()
    {
        contentText.text = pages[currentPage];
        contentText.color = Color.red;  // 设置字体颜色为黑色
        nextButton.interactable = currentPage < pages.Length - 1;
        prevButton.interactable = currentPage > 0;
        Debug.Log("Current Page: " + currentPage);  // 输出当前页码，调试用
    }

    public void NextPage()
    {
        if (currentPage < pages.Length - 1)
        {
            currentPage++;
            UpdatePage();
            Debug.Log("Next Page: " + currentPage);  // 输出调试信息，查看点击后的页码
        }
    }

    public void PrevPage()
    {
        if (currentPage > 0)
        {
            currentPage--;
            UpdatePage();
            Debug.Log("Previous Page: " + currentPage);  // 输出调试信息，查看点击后的页码
        }
    }
}



