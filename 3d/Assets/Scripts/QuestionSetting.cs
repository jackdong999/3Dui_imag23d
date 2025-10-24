using UnityEngine;

public class QuestionSetting : MonoBehaviour
{
    [SerializeField] private GameObject questionLeft;
    [SerializeField] private GameObject questionMiddle;
    [SerializeField] private GameObject questionRight;
    [SerializeField] private GameObject AnswerUI;
    [SerializeField] private GameObject painting;
    void Start()
    {
        questionLeft.SetActive(false);
        questionMiddle.SetActive(false);
        questionRight.SetActive(false);
        AnswerUI.SetActive(false);
        painting.SetActive(false);
    }
}