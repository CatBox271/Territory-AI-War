using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class KimRotator : MonoBehaviour
{
    public Rigidbody2D rb;
    private float angle;
    public float speed = 60;
    public bool right = true;
    private float _speed { get { return speed * (right ? 1 : -1); } }
    void FixedUpdate()
    {
        rb?.MoveRotation(angle);
        angle += Time.fixedDeltaTime * _speed;
    }
}
