using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class WallEditor : MonoBehaviour
{
    [Header("内部大小")]
    public float width = 10;
    public float heigth = 10;
    [Header("墙体宽度")]
    public float wall_width = 0.1f;
    [Header("刷新")]
    public bool refresh;
    public bool widthAsyncHeigth = true;
    private void OnValidate()
    {
        if (widthAsyncHeigth) heigth = width;
        Adjust();
        refresh = false;
    }
    private void Adjust()
    {
        Transform up = transform.GetChild(0);
        Transform down = transform.GetChild(1);
        Transform left = transform.GetChild(2);
        Transform right = transform.GetChild(3);

        Vector3 self = transform.position;
        float x = (width + wall_width) / 2f;
        float y = (heigth + wall_width) / 2f;

        up.position = self + new Vector3(0, y);
        down.position = self + new Vector3(0, -y);
        down.localScale = up.localScale = new Vector3(width + wall_width * 2, wall_width);

        left.position = self + new Vector3(-x, 0);
        right.position = self + new Vector3(x, 0);
        right.localScale = left.localScale = new Vector3(heigth + wall_width * 2, wall_width);
    }
}
