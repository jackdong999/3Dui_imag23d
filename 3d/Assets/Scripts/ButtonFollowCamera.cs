using UnityEngine;

public class FollowCamera : MonoBehaviour
{
    public Transform cameraTransform;
    public float distance = 0.5f;  // 距离摄像机前方多远
    public Vector3 offset = Vector3.zero; // 可以上下左右微调

    void Update()
    {
        if (cameraTransform == null)
        {
            cameraTransform = Camera.main.transform; // 默认跟随主相机
        }

        // 设置位置 = 摄像机位置 + 前方方向 * 距离 + 偏移
        transform.position = cameraTransform.position + cameraTransform.forward * distance + offset;

        // 让按钮一直面向摄像机
        transform.rotation = Quaternion.LookRotation(transform.position - cameraTransform.position);
    }
}

