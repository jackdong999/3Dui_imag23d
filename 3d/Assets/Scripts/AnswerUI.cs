using System;
using UnityEngine;

public class AnswerUI : MonoBehaviour
{
    [SerializeField] private GameObject questionLeft;
    [SerializeField] private GameObject questionMiddle;
    [SerializeField] private GameObject questionRight;
    [SerializeField] private ChangeImageRGB changeImageRGB; 
    [SerializeField] private AudioSource audioSource;
    [SerializeField] private AudioClip correctSound;
    [SerializeField] private AudioClip wrongSound;
    public void Option1()
    {
        if (questionLeft.activeSelf)
        {
        changeImageRGB.AddRed();
        PlayCorrectSound();
        }
        else
        {
            PlayWrongSound();

        }
    }
    private void PlayCorrectSound()
    {
        audioSource.clip = correctSound;
        audioSource.Play();
    }
    private void PlayWrongSound()
    {
        audioSource.clip = wrongSound;
        audioSource.Play();
    }


    public void Option2()
    {
        if (questionMiddle.activeSelf)
        {
        changeImageRGB.AddGreen();
         PlayCorrectSound();
        }
        else
        {
            PlayWrongSound();

        }
    }   
    public void Option3()
    {
        if (questionRight.activeSelf)
        {
        changeImageRGB.AddBlue();
         PlayCorrectSound();
        }
        else
        {
            PlayWrongSound();

        }
    }
}
