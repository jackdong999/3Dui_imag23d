using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class LookAtPlayer : MonoBehaviour
{
    public Transform cameraTransform;
     [SerializeField] public float distanceFromUser = 0.5f;

    // Start is called before the first frame update
    void Start()
    {
        if (cameraTransform == null)
        {
            cameraTransform = Camera.main.transform;
        }
    }

    // Update is called once per frame
    void Update()
    {
        if (cameraTransform != null)
        {
            Vector3 targetPosition = cameraTransform.position + cameraTransform.forward * distanceFromUser;
            transform.position = targetPosition;
            transform.LookAt(cameraTransform);
            transform.Rotate(0, 180, 0);
        }
    }
}
