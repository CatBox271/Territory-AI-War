using System.Collections.Generic;

public class MonoCharacter
{
    public string ai_name = "";//角色的唯一名称
    public string oc = "";//角色设定
    public List<DeepSeekMessage> messages = new();//头个固定为oc

    public List<DeepSeekMessage> Creat(string ai_name, string oc)
    {
        this.ai_name = ai_name;
        this.oc = oc;
        messages.Clear();
        AddContent(oc, "system");
        return messages;
    }
    public void AddContent(string content, string role = "user")
    {
        messages.Add(new DeepSeekMessage(role, content));
    }
}
