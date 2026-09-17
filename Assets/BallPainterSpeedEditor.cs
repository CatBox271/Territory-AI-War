using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class BallPainterSpeedEditor : MonoBehaviour ,IValueEditor
{
    public BallPainter ball;
    public float value { get { return ball.SpeedTimes; } set { ball.SpeedTimes = value; } }
}
