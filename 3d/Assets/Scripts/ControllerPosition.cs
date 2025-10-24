using UnityEngine;
using UnityEngine.XR;

public class GetControllerPosition : MonoBehaviour
{
    [SerializeField] private GameObject obj;
    InputDevice leftController;
    void Start()
    {
        
    }
    void Update()
    {     leftController = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);  
         Debug.Log("leftController: " + leftController);
        if (leftController.TryGetFeatureValue(CommonUsages.triggerButton, out bool isTriggerPressed) && isTriggerPressed)
        {
            Debug.Log("Trigger Pressed111111111111111111111111111111");
            if (leftController.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 position))
            {
                obj.transform.position = position;
                Debug.Log("Left Controller Position: " + position);
            }
        }
    }
}
