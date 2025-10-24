using UnityEngine;
using UnityEngine.SceneManagement;

public class ChangeImageRGB : MonoBehaviour
{
    private SpriteRenderer spriteRenderer;
    private Texture2D grayTex; // 灰度图纹理
    private Color[] originalPixels; // 原图像素
    private Color[] currentPixels;  // 当前显示的像素
    private float rMultiplier = 0f;
    private float gMultiplier = 0f;
    private float bMultiplier = 0f;
    [SerializeField] private GameObject objectToActivate; // 要激活的物体
    private bool activated = false; // 防止重复激活
    void Start()
    {
        spriteRenderer = GetComponent<SpriteRenderer>();

        Sprite originalSprite = spriteRenderer.sprite;
        Texture2D originalTex = originalSprite.texture;
        originalPixels = originalTex.GetPixels();

        grayTex = new Texture2D(originalTex.width, originalTex.height);
        currentPixels = new Color[originalPixels.Length];

        // 把图片变成灰度图
        for (int i = 0; i < originalPixels.Length; i++)
        {
            Color c = originalPixels[i];
            float gray = (c.r + c.g + c.b) / 3f; // 也可以用 0.299/0.587/0.114 做更真实的灰度
            currentPixels[i] = new Color(gray, gray, gray, c.a);
        }

        grayTex.SetPixels(currentPixels);
        grayTex.Apply();

        Sprite newSprite = Sprite.Create(
            grayTex,
            originalSprite.rect,
            new Vector2(0.5f, 0.5f),
            originalSprite.pixelsPerUnit
        );

        spriteRenderer.sprite = newSprite;
    }

    // 供外部调用的方法：分别增加红绿蓝通道
    public void AddRed()
    {
        rMultiplier = Mathf.Clamp01(0.655f);
        UpdateImage();
    }

    public void AddGreen()
    {
        gMultiplier = Mathf.Clamp01(0.58f);
        UpdateImage();
    }

    public void AddBlue()
    {
        bMultiplier = Mathf.Clamp01(0.471f);
        UpdateImage();
    }

    // 更新图片的方法
    private void UpdateImage()
    {
        for (int i = 0; i < originalPixels.Length; i++)
        {
            Color c = originalPixels[i];
            float gray = (c.r + c.g + c.b) / 3f;
            float newR = Mathf.Lerp(gray, c.r, rMultiplier);
            float newG = Mathf.Lerp(gray, c.g, gMultiplier);
            float newB = Mathf.Lerp(gray, c.b, bMultiplier);
            currentPixels[i] = new Color(newR, newG, newB, c.a);
        }
        grayTex.SetPixels(currentPixels);
        grayTex.Apply();

        CheckFullyRecovered();
    }
    private void CheckFullyRecovered()
    {
        if (!activated && rMultiplier >= 0.655f && gMultiplier >= 0.58f && bMultiplier >= 0.471f)
        {
            if (objectToActivate != null)
            {
                objectToActivate.SetActive(true);
                activated = true; // 只激活一次
                SceneManager.LoadScene("EndScene"); // 加载结束场景
            }
        }
    }
}
