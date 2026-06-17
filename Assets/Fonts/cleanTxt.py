def clean_text_file(input_file, output_file=None):
    """
    去除文本文件中的所有空格、换行符和制表符
    
    参数:
        input_file: 输入文件路径
        output_file: 输出文件路径（可选，不指定则覆盖原文件）
    """
    # 读取原文件内容
    with open(input_file, 'r', encoding='utf-8') as f:
        content = f.read()
    
    # 去除所有空白字符：空格、换行符、制表符等
    # 方法1：只去除空格、\n、\r、\t
    cleaned_content = content.replace(' ', '').replace('\n', '').replace('\r', '').replace('\t', '')
    
    # 方法2（备选）：使用正则表达式去除所有空白字符
    # import re
    # cleaned_content = re.sub(r'\s+', '', content)
    
    # 确定输出文件路径
    if output_file is None:
        output_file = input_file
    
    # 写入清理后的内容
    with open(output_file, 'w', encoding='utf-8') as f:
        f.write(cleaned_content)
    
    print(f"处理完成！已清理文件: {output_file}")
    print(f"原文件大小: {len(content)} 字符")
    print(f"清理后大小: {len(cleaned_content)} 字符")

# 使用示例
if __name__ == "__main__":
    
    # 示例2：直接覆盖原文件
    clean_text_file(r"C:\Users\m1872\Desktop\Unity\item\ICE-Bubble\Assets\Fonts\7000常用字优化版.txt")