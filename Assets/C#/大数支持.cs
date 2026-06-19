using System;
using System.Collections.Generic;
using System.Text;

[Serializable]
public struct HugeInt : IComparable, IComparable<HugeInt>, IEquatable<HugeInt>, IFormattable
{
    private readonly List<uint> _digits;
    private readonly bool _isNegative;

    #region 构造函数
    public HugeInt(int value) : this((long)value) { }

    public HugeInt(long value)
    {
        _isNegative = value < 0;
        ulong absValue = value < 0 ? (ulong)(-value) : (ulong)value;

        _digits = new List<uint>();
        while (absValue > 0)
        {
            _digits.Add((uint)(absValue & 0xFFFFFFFF));
            absValue >>= 32;
        }

        if (_digits.Count == 0)
            _digits.Add(0);
    }

    public HugeInt(string value)
    {
        if (string.IsNullOrEmpty(value))
            throw new FormatException("Value cannot be null or empty");

        int startIndex = 0;
        bool isNegative = false;

        if (value[0] == '-')
        {
            isNegative = true;
            startIndex = 1;
        }
        else if (value[0] == '+')
        {
            startIndex = 1;
        }

        _digits = new List<uint> { 0 };
        _isNegative = isNegative;

        for (int i = startIndex; i < value.Length; i++)
        {
            char c = value[i];
            if (c < '0' || c > '9')
                throw new FormatException($"Invalid character '{c}' in number");

            int digit = c - '0';
            this = this * 10 + digit;
        }

        if (isNegative)
        {
            this = -this;
        }
    }

    private HugeInt(List<uint> digits, bool isNegative)
    {
        _digits = TrimLeadingZeros(digits);
        _isNegative = _digits.Count == 1 && _digits[0] == 0 ? false : isNegative;
    }
    #endregion

    #region 算术运算符
    public static HugeInt operator +(HugeInt left, HugeInt right)
    {
        if (left._isNegative == right._isNegative)
        {
            var resultDigits = Add(left._digits, right._digits);
            return new HugeInt(resultDigits, left._isNegative);
        }

        int comparison = CompareAbsolute(left._digits, right._digits);
        if (comparison == 0) return Zero;

        if (comparison > 0)
        {
            var resultDigits = Subtract(left._digits, right._digits);
            return new HugeInt(resultDigits, left._isNegative);
        }
        else
        {
            var resultDigits = Subtract(right._digits, left._digits);
            return new HugeInt(resultDigits, right._isNegative);
        }
    }

    public static HugeInt operator -(HugeInt left, HugeInt right)
    {
        return left + (-right);
    }

    public static HugeInt operator *(HugeInt left, HugeInt right)
    {
        if (left == Zero || right == Zero) return Zero;

        var resultDigits = Multiply(left._digits, right._digits);
        bool resultNegative = left._isNegative != right._isNegative;
        return new HugeInt(resultDigits, resultNegative);
    }

    public static HugeInt operator /(HugeInt dividend, HugeInt divisor)
    {
        if (divisor == Zero)
            throw new DivideByZeroException();

        var result = Divide(dividend, divisor, out _);
        return result;
    }

    public static HugeInt operator %(HugeInt dividend, HugeInt divisor)
    {
        if (divisor == Zero)
            throw new DivideByZeroException();

        Divide(dividend, divisor, out HugeInt remainder);
        return remainder;
    }

    public static HugeInt operator /(HugeInt dividend, int divisor) => dividend / (HugeInt)divisor;
    public static HugeInt operator /(HugeInt dividend, long divisor) => dividend / (HugeInt)divisor;
    public static float operator /(HugeInt dividend, float divisor)
    {
        return dividend.ToFloat() / divisor;
    }

    public static HugeInt operator -(HugeInt value)
    {
        if (value == Zero) return Zero;
        return new HugeInt(value._digits, !value._isNegative);
    }

    public static HugeInt operator ++(HugeInt value)
    {
        return value + One;
    }

    public static HugeInt operator --(HugeInt value)
    {
        return value - One;
    }
    #endregion

    #region 比较运算符
    public static bool operator ==(HugeInt left, HugeInt right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(HugeInt left, HugeInt right)
    {
        return !left.Equals(right);
    }

    public static bool operator <(HugeInt left, HugeInt right)
    {
        return left.CompareTo(right) < 0;
    }

    public static bool operator >(HugeInt left, HugeInt right)
    {
        return left.CompareTo(right) > 0;
    }

    public static bool operator <=(HugeInt left, HugeInt right)
    {
        return left.CompareTo(right) <= 0;
    }

    public static bool operator >=(HugeInt left, HugeInt right)
    {
        return left.CompareTo(right) >= 0;
    }
    #endregion

    #region 类型转换运算符
    public static implicit operator HugeInt(int value) => new HugeInt(value);
    public static implicit operator HugeInt(long value) => new HugeInt(value);
    public static explicit operator int(HugeInt value) => (int)value.ToLong();
    public static explicit operator long(HugeInt value) => value.ToLong();
    #endregion

    #region 核心算法实现
    private static List<uint> Add(List<uint> left, List<uint> right)
    {
        List<uint> result = new List<uint>();
        ulong carry = 0;
        int maxLength = Math.Max(left.Count, right.Count);

        for (int i = 0; i < maxLength || carry > 0; i++)
        {
            ulong sum = carry;
            if (i < left.Count) sum += left[i];
            if (i < right.Count) sum += right[i];

            result.Add((uint)(sum & 0xFFFFFFFF));
            // 修复：基数是 2^32，进位应当右移 32 位，而不是除以 10
            carry = sum >> 32;
        }

        return result;
    }

    private static List<uint> Subtract(List<uint> left, List<uint> right)
    {
        List<uint> result = new List<uint>();
        ulong borrow = 0;

        for (int i = 0; i < left.Count; i++)
        {
            ulong leftDigit = left[i];
            ulong rightDigit = (i < right.Count) ? right[i] : 0;

            if (leftDigit < rightDigit + borrow)
            {
                leftDigit += 0x100000000;
                result.Add((uint)(leftDigit - rightDigit - borrow));
                borrow = 1;
            }
            else
            {
                result.Add((uint)(leftDigit - rightDigit - borrow));
                borrow = 0;
            }
        }

        return TrimLeadingZeros(result);
    }

    private static List<uint> Multiply(List<uint> left, List<uint> right)
    {
        List<uint> result = new List<uint>(new uint[left.Count + right.Count]);

        for (int i = 0; i < left.Count; i++)
        {
            ulong carry = 0;
            for (int j = 0; j < right.Count || carry > 0; j++)
            {
                int index = i + j;
                ulong product = result[index] + (ulong)left[i] * (j < right.Count ? right[j] : 0) + carry;
                result[index] = (uint)(product & 0xFFFFFFFF);
                carry = product >> 32;
            }
        }

        return TrimLeadingZeros(result);
    }

    private static HugeInt Divide(HugeInt dividend, HugeInt divisor, out HugeInt remainder)
    {
        remainder = Zero;

        if (dividend._isNegative || divisor._isNegative)
        {
            HugeInt absDividend = dividend._isNegative ? -dividend : dividend;
            HugeInt absDivisor = divisor._isNegative ? -divisor : divisor;

            var result = DivideUnsigned(absDividend, absDivisor, out HugeInt rem);

            bool resultNegative = dividend._isNegative != divisor._isNegative;
            bool remainderNegative = dividend._isNegative;

            remainder = remainderNegative ? -rem : rem;
            return new HugeInt(result._digits, resultNegative);
        }

        return DivideUnsigned(dividend, divisor, out remainder);
    }

    private static HugeInt DivideUnsigned(HugeInt dividend, HugeInt divisor, out HugeInt remainder)
    {
        remainder = Zero;

        if (CompareAbsolute(dividend._digits, divisor._digits) < 0)
        {
            remainder = dividend;
            return Zero;
        }

        if (divisor == One)
        {
            remainder = Zero;
            return dividend;
        }

        List<uint> quotient = new List<uint>();
        List<uint> current = new List<uint>();

        for (int i = dividend._digits.Count - 1; i >= 0; i--)
        {
            current.Insert(0, dividend._digits[i]);
            current = TrimLeadingZeros(current);

            if (CompareAbsolute(current, divisor._digits) < 0)
            {
                quotient.Insert(0, 0u);
                continue;
            }

            uint digit = BinarySearchDivide(current, divisor._digits);
            quotient.Insert(0, digit);

            var product = Multiply(divisor._digits, new List<uint> { digit });
            current = Subtract(current, product);
        }

        remainder = new HugeInt(current, false);
        return new HugeInt(quotient, false);
    }

    private static uint BinarySearchDivide(List<uint> dividend, List<uint> divisor)
    {
        uint low = 0;
        uint high = 0xFFFFFFFF;

        while (low <= high)
        {
            uint mid = low + (high - low) / 2;
            var product = Multiply(divisor, new List<uint> { mid });

            int comparison = CompareAbsolute(product, dividend);
            if (comparison <= 0)
            {
                var nextProduct = Multiply(divisor, new List<uint> { mid + 1 });
                if (CompareAbsolute(nextProduct, dividend) > 0)
                {
                    return mid;
                }
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return high;
    }

    private static List<uint> TrimLeadingZeros(List<uint> digits)
    {
        List<uint> result = new List<uint>(digits);
        while (result.Count > 1 && result[result.Count - 1] == 0)
        {
            result.RemoveAt(result.Count - 1);
        }
        return result;
    }

    private static int CompareAbsolute(List<uint> left, List<uint> right)
    {
        if (left == null) left = new List<uint> { 0 };
        if (right == null) right = new List<uint> { 0 };
        if (left.Count != right.Count)
            return left.Count.CompareTo(right.Count);

        for (int i = left.Count - 1; i >= 0; i--)
        {
            if (left[i] != right[i])
                return left[i].CompareTo(right[i]);
        }

        return 0;
    }
    #endregion

    #region 接口实现与重写方法
    public int CompareTo(object obj)
    {
        if (obj is HugeInt other)
            return CompareTo(other);
        throw new ArgumentException("Object is not a HugeInt");
    }

    public int CompareTo(HugeInt other)
    {
        if (_isNegative != other._isNegative)
            return _isNegative ? -1 : 1;

        int absoluteComparison = CompareAbsolute(_digits, other._digits);
        return _isNegative ? -absoluteComparison : absoluteComparison;
    }

    public bool Equals(HugeInt other)
    {
        return _isNegative == other._isNegative &&
               CompareAbsolute(_digits, other._digits) == 0;
    }

    public override bool Equals(object obj)
    {
        return obj is HugeInt other && Equals(other);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + _isNegative.GetHashCode();
            foreach (uint digit in _digits)
            {
                hash = hash * 31 + digit.GetHashCode();
            }
            return hash;
        }
    }

    public override string ToString()
    {
        var sb = new StringBuilder();
        ToStringBuilder(sb);
        return sb.ToString();
    }

    /// <summary>写入已有 StringBuilder。用 10^9 为大除数，一次出 9 位，Divide 调用减少 ~9 倍。</summary>
    public void ToStringBuilder(StringBuilder sb)
    {
        if (this == Zero) { sb.Append('0'); return; }

        HugeInt current = _isNegative ? -this : this;
        var remainders = new uint[32]; // 最多 256 位数字 ÷ 9 ≈ 29 段
        int remCount = 0;

        while (current != Zero)
        {
            // 用 10^9 替代 10，一次提取 9 位十进制数
            if (current < TenToNine)
            {
                remainders[remCount++] = current._digits[0];
                break;
            }
            var div = Divide(current, TenToNine, out HugeInt remainder);
            remainders[remCount++] = remainder._digits[0]; // < 10^9 < 2^32
            current = div;
        }

        if (_isNegative) sb.Append('-');

        // 最高段无前导零，其余补足 9 位
        for (int i = remCount - 1; i >= 0; i--)
            AppendUIntGroup(sb, remainders[i], i == remCount - 1 ? 0 : 9);
    }

    /// <summary>将 uint v 转为十进制写入 sb，minDigits > 0 时不足补前导零。</summary>
    static void AppendUIntGroup(StringBuilder sb, uint v, int minDigits)
    {
        int start = sb.Length;
        if (v == 0 && minDigits <= 1) { sb.Append('0'); return; }

        while (v > 0)
        {
            sb.Append((char)('0' + v % 10));
            v /= 10;
        }

        while (sb.Length - start < minDigits)
            sb.Append('0');

        // 反转
        int end = sb.Length - 1;
        while (start < end)
        {
            char t = sb[start];
            sb[start] = sb[end];
            sb[end] = t;
            start++; end--;
        }
    }

    public string ToString(string format, IFormatProvider formatProvider)
    {
        return ToString();
    }
    #endregion

    #region 实用方法与常量
    public long ToLong()
    {
        if (CompareAbsolute(_digits, new HugeInt(long.MaxValue)._digits) > 0)
            throw new OverflowException("Value is too large for long");

        long result = 0;
        for (int i = _digits.Count - 1; i >= 0; i--)
        {
            result = (result << 32) + _digits[i];
        }
        return _isNegative ? -result : result;
    }

    public float ToFloat()
    {
        if (this == Zero) return 0f;
        double log2 = Log2(this);
        double val = System.Math.Pow(2.0, log2);
        return (float)(_isNegative ? -val : val);
    }

    public static HugeInt Zero => new HugeInt(0);
    public static HugeInt One => new HugeInt(1);
    public static HugeInt Ten => new HugeInt(10);
    private static HugeInt TenToNine = new HugeInt(1000000000);

    public static HugeInt Parse(string value) => new HugeInt(value);
    public static bool TryParse(string value, out HugeInt result)
    {
        try
        {
            result = new HugeInt(value);
            return true;
        }
        catch
        {
            result = Zero;
            return false;
        }
    }

    public static HugeInt Pow(HugeInt value, int exponent)
    {
        if (exponent < 0)
            throw new ArgumentException("Exponent must be non-negative");

        if (exponent == 0) return One;
        if (exponent == 1) return value;

        HugeInt result = One;
        HugeInt baseValue = value;

        while (exponent > 0)
        {
            if ((exponent & 1) == 1)
                result *= baseValue;

            baseValue *= baseValue;
            exponent >>= 1;
        }

        return result;
    }

    /// <summary>
    /// 计算以 2 为底的对数（浮点），例如 Log2(8) = 3.0, Log2(10) ≈ 3.3219。
    /// </summary>
    public static float Log2(HugeInt value)
    {
        if (value <= Zero)
            throw new ArgumentException("Value must be positive");

        var digits = value._digits;
        int msdIndex = digits.Count - 1;
        uint msd = digits[msdIndex];

        return msdIndex * 32f + (float)System.Math.Log(msd, 2.0);
    }
    #endregion

    // 自动保留一位小数（如 1.2K、9.9M）
    public string ToShortString() => ToShortString(false);
    // 指定是否省略小数点
    public string ToShortString(bool nopoint)
    {
        HugeInt absValue = this < Zero ? -this : this;   // 只显示绝对值
        if (absValue < 1000)
            return absValue.ToString();
        HugeInt kBase = 1000;
        HugeInt mBase = 1000000;
        HugeInt bBase = 1000000000;
        HugeInt tBase = 1000000000000;
        HugeInt pBase = 1000000000000000;
        HugeInt baseValue;
        string suffix;
        if (absValue < mBase) { baseValue = kBase; suffix = "K"; }
        else if (absValue < bBase) { baseValue = mBase; suffix = "M"; }
        else if (absValue < tBase) { baseValue = bBase; suffix = "B"; }
        else if (absValue < pBase) { baseValue = tBase; suffix = "T"; }
        else { baseValue = pBase; suffix = "P"; }
        // 大于等于 10*基数 或要求不显示小数 → 直接取整
        if (nopoint || absValue >= baseValue * 10)
            return (absValue / baseValue).ToString() + suffix;
        // 保留一位小数：计算 absValue / (基数/10) 并在倒数第二位前插入小数点
        HugeInt scaled = absValue / (baseValue / 10);    // 结果在 10~99 之间
        string scaledStr = scaled.ToString();
        return scaledStr.Insert(scaledStr.Length - 1, ".") + suffix;
    }
}