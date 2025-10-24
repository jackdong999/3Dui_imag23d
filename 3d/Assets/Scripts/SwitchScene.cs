using UnityEngine;
using UnityEngine.SceneManagement;

public class MenuUI : MonoBehaviour
{
    public void StartGame()
    {
        // 加载主场景
        Debug.Log("clicked start game");
        SceneManager.LoadScene("GameScene"); // 替换为你的主场景名
    }

    public void QuitGame()
    {
        // 退出游戏
        Application.Quit();
        Debug.Log("Quit Game");
    }
}

