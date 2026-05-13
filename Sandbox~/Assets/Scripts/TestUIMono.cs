using System;
using TMPro;
using UnityEngine;

public class TestUIMono : MonoBehaviour
{
    [SerializeField] private TMP_Text _txtNum;

    private int _currNum;

    private void OnEnable()
    {
        _txtNum.text = _currNum.ToString();
    }

    public void OnClickAdd()
    {
        _currNum++;
        _txtNum.text = _currNum.ToString();
    }
}