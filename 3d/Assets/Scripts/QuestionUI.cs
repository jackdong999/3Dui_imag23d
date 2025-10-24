using UnityEngine;

public class QuestionUI : MonoBehaviour
{
    [SerializeField] private GameObject questionLeft;
    [SerializeField] private GameObject questionMiddle;
    [SerializeField] private GameObject questionRight;
    [SerializeField] private GameObject AnswerUI;
    public void BookLeft()
    {   
        questionMiddle.SetActive(false);
        questionRight.SetActive(false);
        questionLeft.SetActive(true);
        AnswerUI.SetActive(true);
        // if (questionLeft.activeSelf)
        // {
        //     questionLeft.SetActive(false);
        //     AnswerUI.SetActive(false);
        // }
        // else
        // {
        //     questionLeft.SetActive(true);
        //     AnswerUI.SetActive(true);
        // }
    }
    public void BookMiddle()
    {
        questionLeft.SetActive(false);
        questionRight.SetActive(false);
        questionMiddle.SetActive(true);
        AnswerUI.SetActive(true);
        // if (questionMiddle.activeSelf)
        // {
        //     questionMiddle.SetActive(false);
        //     AnswerUI.SetActive(false);
        // }
        // else
        // {
        //     questionMiddle.SetActive(true);
        //     AnswerUI.SetActive(true);
        // }
    }

    public void BookRight()
    {   
        questionLeft.SetActive(false);
        questionMiddle.SetActive(false);
        questionRight.SetActive(true);
        AnswerUI.SetActive(true);
        // if (questionRight.activeSelf)
        // {
        //     questionRight.SetActive(false);
        //     AnswerUI.SetActive(false);
        // }
        // else
        // {
        //     questionRight.SetActive(true);
        //     AnswerUI.SetActive(true);
        // }
    }
}

