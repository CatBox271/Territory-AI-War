using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using static DeepSeekClient;

public class MonoCharacter
{
    public string ai_name = "";//角色的唯一名称
    public string oc = "";//角色设定
    public List<ChatMsg> messages = new();//头个固定为oc

    public List<ChatMsg> Creat(string ai_name,string oc)
    {
        this.ai_name = ai_name;
        this.oc = oc;
        messages.Clear();
        AddContent(oc,"system");
        return messages;
    }
    public void AddContent(string content,string role = "user")
    {
        messages.Add(new ChatMsg() { Role = role, Content = content });
    }
}
