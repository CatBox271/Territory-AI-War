using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class BallPainterGuidGetter : MonoBehaviour, IStringGetter
{
    public BallPainter ball;
    public string stringInfo { get { return ball.guid; } }
}
