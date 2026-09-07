using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class LeftRight : MonoBehaviour
{
    public float speed;
    public Vector2 left_right = new(-5, 5);
    public Rigidbody2D rb;
    private float kt = 0;
    private Vector2 offset = new();

    private void Awake()
    {
        offset = transform.position - transform.localPosition;//在父物体没有缩放的情况下
    }
    private float PI2 = Mathf.PI * 2;
    private void FixedUpdate()
    {
        kt += Time.fixedDeltaTime * speed;
        rb.MovePosition(new Vector2(Mathf.Lerp(left_right[0], left_right[1], (Mathf.Sin(kt) + 1) / 2f), transform.position.y) + offset);
        if (kt > PI2) kt -= PI2;
    }
}
