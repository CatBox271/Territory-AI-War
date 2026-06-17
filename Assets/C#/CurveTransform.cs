using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[System.Serializable]
public class CurveTransform
{
    public AnimationCurve curve;

    public float Evaluate(float input)
    {
        float a = Mathf.Log(Mathf.Max(1, input), 2);
        if (a > maxTime) return maxValue;
        if (a < minTime) return minValue;
        return curve.Evaluate(a);
    }
    public float Evaluate(HugeInt input)
    {
        if (input < 1) return minValue;
        float a = HugeInt.Log2(input);
        if (a > maxTime) return maxValue;
        if (a < minTime) return minValue;
        return curve.Evaluate(a);
    }
    #region »ù´¡¹¤¾ß
    private float _maxTime = float.NaN;
    public float maxTime 
    {
        get
        {
            if (_maxTime == float.NaN)
            {
                _maxTime = curve.keys[^1].time;
            }
            return _maxTime;
        }
    }
    private float _minTime = float.NaN;
    public float minTime
    {
        get
        {
            if (_minTime == float.NaN)
            {
                _minTime = curve.keys[0].time;
            }
            return _minTime;
        }
    }
    private float _maxValue = float.NaN;
    public float maxValue
    {
        get
        {
            if (_maxValue == float.NaN)
            {
                _maxValue = curve.keys[^1].value;
            }
            return _maxValue;
        }
    }
    private float _minValue = float.NaN;
    public float minValue
    {
        get
        {
            if (_minValue == float.NaN)
            {
                _minValue = curve.keys[0].value;
            }
            return _minValue;
        }
    }
    #endregion
}
