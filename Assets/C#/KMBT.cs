using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

public class KMBT : MonoBehaviour
{
    public static string NumChange(long numin)
    {
        StringBuilder stringBuilder = new();
        if (numin < 1000)
        {

            stringBuilder.Append(numin);
        }
        else
        {
            if (numin < 1000000)
            {
                stringBuilder.Append(numin / 1000);
                stringBuilder.Append("K");
            }
            else
            {
                if (numin < 1000000000)
                {
                    if (numin < 10000000)//保留一位小数点
                    {
                        stringBuilder.Append(numin / 100000);
                        stringBuilder.Insert(stringBuilder.Length - 1, ".");
                    }
                    else
                    {
                        stringBuilder.Append(numin / 1000000);
                    }
                    stringBuilder.Append("M");
                }
                else
                {
                    if (numin < 1000000000000)
                    {
                        if (numin < 10000000000)//保留一位小数点
                        {
                            stringBuilder.Append(numin / 100000000);
                            stringBuilder.Insert(stringBuilder.Length - 1, ".");
                        }
                        else
                        {
                            stringBuilder.Append(numin / 1000000000);
                        }
                        stringBuilder.Append("B");
                    }
                    else
                    {
                        if (numin < 1000000000000)//保留一位小数点
                        {
                            stringBuilder.Append(numin / 100000000000);
                            stringBuilder.Insert(stringBuilder.Length - 1, ".");
                        }
                        else
                        {
                            stringBuilder.Append(numin / 1000000000000);
                        }
                        stringBuilder.Append("T");
                    }
                }
            }
        }
        return stringBuilder.ToString();
    }

    public static string NumChange(long numin,bool nopoint)
    {
        StringBuilder stringBuilder = new();
        if (numin < 1000)
        {

            stringBuilder.Append(numin);
        }
        else
        {
            if (numin < 1000000)
            {
                stringBuilder.Append(numin / 1000);
                stringBuilder.Append("K");
            }
            else
            {
                if (numin < 1000000000)
                {
                    stringBuilder.Append(numin / 1000000);
                    stringBuilder.Append("M");
                }
                else
                {
                    if (numin < 1000000000000)
                    {
                        stringBuilder.Append(numin / 1000000000);
                        stringBuilder.Append("B");
                    }
                    else
                    {
                        stringBuilder.Append(numin / 1000000000000);
                        stringBuilder.Append("T");
                    }
                }
            }
        }
        return stringBuilder.ToString();
    }
}
