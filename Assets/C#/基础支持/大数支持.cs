using System;
using System.Collections.Generic;
using System.Text;

[Serializable]
public struct HugeInt : IComparable, IComparable<HugeInt>, IEquatable<HugeInt>, IFormattable
{
    private readonly List<byte> _digits;
    private readonly bool _isNegative;

    private static readonly List<byte> ZeroBytes = new List<byte> { 0 };
    private static readonly uint[] PowersOfTen = { 1u, 10u, 100u, 1000u, 10000u, 100000u, 1000000u, 10000000u, 100000000u, 1000000000u };

    #region 构造函数
    public HugeInt(int value) : this((long)value) { }

    public HugeInt(long value)
    {
        bool negative = value < 0;
        ulong magnitude;
        if (value < 0)
            magnitude = value == long.MinValue ? 1UL << 63 : (ulong)(-value);
        else
            magnitude = (ulong)value;

        _digits = new List<byte>();
        while (magnitude > 0)
        {
            _digits.Add((byte)(magnitude & 0xFF));
            magnitude >>= 8;
        }

        if (_digits.Count == 0)
            _digits.Add(0);

        _isNegative = negative;
    }

    public HugeInt(string value)
    {
        _digits = null;
        _isNegative = false;

        if (string.IsNullOrEmpty(value))
            throw new FormatException("Value cannot be null or empty");

        int startIndex = 0;
        bool negative = false;
        if (value[0] == '-')
        {
            negative = true;
            startIndex = 1;
        }
        else if (value[0] == '+')
        {
            startIndex = 1;
        }

        List<byte> digits = new List<byte> { 0 };
        int position = startIndex;
        while (position < value.Length)
        {
            int chunkLength = Math.Min(9, value.Length - position);
            uint chunk = 0;
            for (int i = 0; i < chunkLength; i++)
            {
                char c = value[position + i];
                if (c < '0' || c > '9')
                    throw new FormatException($"Invalid character '{c}' in number");

                chunk = chunk * 10u + (uint)(c - '0');
            }

            // 每 9 位十进制数作为一块，减少大数乘法的次数
            MultiplyBytesByUIntInPlace(digits, PowersOfTen[chunkLength]);
            AddSmallInPlace(digits, chunk);
            position += chunkLength;
        }

        NormalizeInPlace(digits);
        _digits = digits;
        _isNegative = negative && !IsZero(digits);
    }

    /// <summary>私有构造：直接接管 digits（调用方不得继续使用），并规范化。</summary>
    private HugeInt(List<byte> digits, bool isNegative)
    {
        if (digits == null)
            digits = new List<byte> { 0 };

        _digits = digits;
        NormalizeInPlace(_digits);
        _isNegative = isNegative && !IsZero(_digits);
    }
    #endregion

    #region 算术运算符
    public static HugeInt operator +(HugeInt left, HugeInt right)
    {
        List<byte> leftDigits = GetDigits(left);
        List<byte> rightDigits = GetDigits(right);
        bool leftNegative = IsNegativeValue(left);
        bool rightNegative = IsNegativeValue(right);

        if (leftNegative == rightNegative)
            return new HugeInt(AddBytes(leftDigits, rightDigits), leftNegative);

        int comparison = CompareAbsolute(leftDigits, rightDigits);
        if (comparison == 0)
            return Zero;

        if (comparison > 0)
            return new HugeInt(SubtractBytes(leftDigits, rightDigits), leftNegative);

        return new HugeInt(SubtractBytes(rightDigits, leftDigits), rightNegative);
    }

    public static HugeInt operator -(HugeInt left, HugeInt right)
    {
        return left + (-right);
    }

    public static HugeInt operator *(HugeInt left, HugeInt right)
    {
        if (left == Zero || right == Zero)
            return Zero;

        var resultDigits = MultiplyBytes(GetDigits(left), GetDigits(right));
        bool resultNegative = IsNegativeValue(left) != IsNegativeValue(right);
        return new HugeInt(resultDigits, resultNegative);
    }

    public static HugeInt operator /(HugeInt dividend, HugeInt divisor)
    {
        if (divisor == Zero)
            throw new DivideByZeroException();

        return Divide(dividend, divisor, out HugeInt remainder);
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

    /// <summary>
    /// HugeInt 除以 float，结果仍是 HugeInt（向零截断的整数商）。
    /// float 先被精确拆成 尾数 * 2^指数，再使用移位和 uint 除法，避免大数乘法溢出与精度丢失。
    /// </summary>
    public HugeInt Divide(float divisor)
    {
        if (float.IsNaN(divisor) || float.IsInfinity(divisor))
            throw new ArgumentException("Divisor must be a finite number", nameof(divisor));
        if (divisor == 0f)
            throw new DivideByZeroException();

        int bits = System.BitConverter.ToInt32(System.BitConverter.GetBytes(divisor), 0);
        bool divisorNegative = (bits & unchecked((int)0x80000000)) != 0;
        int exponentBits = (bits >> 23) & 0xFF;
        int mantissaBits = bits & 0x7FFFFF;

        ulong significand;
        int powerOfTwo;
        if (exponentBits == 0)
        {
            significand = (ulong)mantissaBits;
            powerOfTwo = -149;
        }
        else
        {
            significand = 0x800000u | (uint)mantissaBits;
            powerOfTwo = exponentBits - 127 - 23;
        }

        List<byte> numerator = new List<byte>(GetDigits(this));
        if (powerOfTwo >= 0)
            numerator = ShiftRight(numerator, powerOfTwo);
        else
            numerator = ShiftLeft(numerator, -powerOfTwo);

        uint ignoredRemainder;
        List<byte> quotientDigits = DivideBytesByUInt(numerator, (uint)significand, out ignoredRemainder);
        return new HugeInt(quotientDigits, IsNegativeValue(this) ^ divisorNegative);
    }

    /// <summary>
    /// HugeInt 乘以 float，结果仍是 HugeInt（向零截断的整数积）。
    /// 先乘尾数，再按 2 的幂移位，避免把 float 放大成巨型整数。
    /// </summary>
    public HugeInt Multiply(float multiplier)
    {
        if (float.IsNaN(multiplier) || float.IsInfinity(multiplier))
            throw new ArgumentException("Multiplier must be a finite number", nameof(multiplier));
        if (multiplier == 0f)
            return Zero;

        int bits = System.BitConverter.ToInt32(System.BitConverter.GetBytes(multiplier), 0);
        bool multiplierNegative = (bits & unchecked((int)0x80000000)) != 0;
        int exponentBits = (bits >> 23) & 0xFF;
        int mantissaBits = bits & 0x7FFFFF;

        ulong significand;
        int powerOfTwo;
        if (exponentBits == 0)
        {
            significand = (ulong)mantissaBits;
            powerOfTwo = -149;
        }
        else
        {
            significand = 0x800000u | (uint)mantissaBits;
            powerOfTwo = exponentBits - 127 - 23;
        }

        List<byte> resultDigits = MultiplyBytesByUInt(GetDigits(this), (uint)significand);
        if (powerOfTwo >= 0)
            resultDigits = ShiftLeft(resultDigits, powerOfTwo);
        else
            resultDigits = ShiftRight(resultDigits, -powerOfTwo);

        return new HugeInt(resultDigits, IsNegativeValue(this) ^ multiplierNegative);
    }

    public static HugeInt operator -(HugeInt value)
    {
        if (IsZero(GetDigits(value)))
            return Zero;

        return new HugeInt(new List<byte>(GetDigits(value)), !IsNegativeValue(value));
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
    private static List<byte> GetDigits(HugeInt value)
    {
        return value._digits ?? ZeroBytes;
    }

    private static bool IsNegativeValue(HugeInt value)
    {
        return value._isNegative && !IsZero(GetDigits(value));
    }

    private static bool IsZero(List<byte> digits)
    {
        return digits == null || (digits.Count == 1 && digits[0] == 0);
    }

    private static void NormalizeInPlace(List<byte> digits)
    {
        if (digits == null)
            return;

        while (digits.Count > 1 && digits[digits.Count - 1] == 0)
            digits.RemoveAt(digits.Count - 1);

        if (digits.Count == 0)
            digits.Add(0);
    }

    private static List<byte> AddBytes(List<byte> left, List<byte> right)
    {
        List<byte> result = new List<byte>(Math.Max(left.Count, right.Count) + 1);
        int carry = 0;
        int maxLength = Math.Max(left.Count, right.Count);

        for (int i = 0; i < maxLength || carry > 0; i++)
        {
            int sum = carry;
            if (i < left.Count) sum += left[i];
            if (i < right.Count) sum += right[i];

            result.Add((byte)(sum & 0xFF));
            carry = sum >> 8;
        }

        return result;
    }

    private static List<byte> SubtractBytes(List<byte> left, List<byte> right)
    {
        List<byte> result = new List<byte>(left.Count);
        int borrow = 0;

        for (int i = 0; i < left.Count; i++)
        {
            int diff = left[i] - borrow;
            if (i < right.Count)
                diff -= right[i];

            if (diff < 0)
            {
                diff += 0x100;
                borrow = 1;
            }
            else
            {
                borrow = 0;
            }

            result.Add((byte)diff);
        }

        NormalizeInPlace(result);
        return result;
    }

    private static List<byte> MultiplyBytes(List<byte> left, List<byte> right)
    {
        if (left.Count == 1)
            return MultiplyBytesByUInt(right, left[0]);
        if (right.Count == 1)
            return MultiplyBytesByUInt(left, right[0]);

        byte[] product = new byte[left.Count + right.Count];

        for (int i = 0; i < left.Count; i++)
        {
            int carry = 0;
            for (int j = 0; j < right.Count; j++)
            {
                int index = i + j;
                int sum = product[index] + left[i] * right[j] + carry;
                product[index] = (byte)(sum & 0xFF);
                carry = sum >> 8;
            }

            int carryIndex = i + right.Count;
            while (carry > 0)
            {
                int sum = product[carryIndex] + carry;
                product[carryIndex] = (byte)(sum & 0xFF);
                carry = sum >> 8;
                carryIndex++;
            }
        }

        List<byte> result = new List<byte>(product);
        NormalizeInPlace(result);
        return result;
    }

    private static List<byte> MultiplyBytesByUInt(List<byte> digits, uint multiplier)
    {
        List<byte> result = new List<byte>(digits);
        MultiplyBytesByUIntInPlace(result, multiplier);
        return result;
    }

    private static void MultiplyBytesByUIntInPlace(List<byte> digits, uint multiplier)
    {
        uint carry = 0;
        for (int i = 0; i < digits.Count; i++)
        {
            ulong value = (ulong)digits[i] * multiplier + carry;
            digits[i] = (byte)(value & 0xFF);
            carry = (uint)(value >> 8);
        }

        while (carry > 0)
        {
            digits.Add((byte)(carry & 0xFF));
            carry >>= 8;
        }

        NormalizeInPlace(digits);
    }

    private static void AddSmallInPlace(List<byte> digits, uint addend)
    {
        if (addend == 0)
            return;

        uint carry = addend;
        int index = 0;
        while (carry > 0)
        {
            if (index == digits.Count)
                digits.Add(0);

            uint sum = digits[index] + carry;
            digits[index] = (byte)(sum & 0xFF);
            carry = sum >> 8;
            index++;
        }
    }

    private static int CompareAbsolute(List<byte> left, List<byte> right)
    {
        if (left == null) left = ZeroBytes;
        if (right == null) right = ZeroBytes;

        if (left.Count != right.Count)
            return left.Count.CompareTo(right.Count);

        for (int i = left.Count - 1; i >= 0; i--)
        {
            if (left[i] != right[i])
                return left[i].CompareTo(right[i]);
        }

        return 0;
    }

    private static bool TryToUInt32(List<byte> digits, out uint value)
    {
        value = 0;
        if (digits == null || digits.Count > 4)
            return false;

        for (int i = digits.Count - 1; i >= 0; i--)
            value = (value << 8) | digits[i];

        return true;
    }

    private static List<byte> FromUInt32(uint value)
    {
        List<byte> result = new List<byte>(4);
        do
        {
            result.Add((byte)(value & 0xFF));
            value >>= 8;
        }
        while (value > 0);

        return result;
    }

    private static List<byte> DivideBytesByUInt(List<byte> digits, uint divisor, out uint remainder)
    {
        if (divisor == 0)
            throw new DivideByZeroException();

        byte[] quotient = new byte[digits.Count];
        ulong current = 0;

        for (int i = digits.Count - 1; i >= 0; i--)
        {
            current = (current << 8) | digits[i];
            quotient[i] = (byte)(current / divisor);
            current %= divisor;
        }

        remainder = (uint)current;
        List<byte> result = new List<byte>(quotient);
        NormalizeInPlace(result);
        return result;
    }

    /// <summary>原地除以 uint，返回余数。用于 ToString 高频路径，一次扫一遍字节，且不产生额外大数。</summary>
    private static uint DivideBytesByUIntInPlace(List<byte> digits, uint divisor)
    {
        ulong current = 0;
        for (int i = digits.Count - 1; i >= 0; i--)
        {
            current = (current << 8) | digits[i];
            digits[i] = (byte)(current / divisor);
            current %= divisor;
        }

        NormalizeInPlace(digits);
        return (uint)current;
    }

    private static List<byte> ShiftLeft(List<byte> digits, int bitCount)
    {
        if (bitCount < 0)
            return ShiftRight(digits, -bitCount);
        if (bitCount == 0)
            return new List<byte>(digits);

        int wholeBytes = bitCount >> 3;
        int remainderBits = bitCount & 7;

        List<byte> result = new List<byte>(digits.Count + wholeBytes + 1);
        for (int i = 0; i < digits.Count + wholeBytes + 1; i++)
            result.Add(0);

        for (int i = 0; i < digits.Count; i++)
        {
            int value = digits[i];
            if (remainderBits == 0)
            {
                result[i + wholeBytes] = (byte)value;
                continue;
            }

            result[i + wholeBytes] = (byte)(result[i + wholeBytes] | ((value << remainderBits) & 0xFF));
            result[i + wholeBytes + 1] = (byte)(result[i + wholeBytes + 1] | (value >> (8 - remainderBits)));
        }

        NormalizeInPlace(result);
        return result;
    }

    private static List<byte> ShiftRight(List<byte> digits, int bitCount)
    {
        if (bitCount < 0)
            return ShiftLeft(digits, -bitCount);
        if (bitCount == 0)
            return new List<byte>(digits);

        int wholeBytes = bitCount >> 3;
        int remainderBits = bitCount & 7;

        if (wholeBytes >= digits.Count)
            return new List<byte> { 0 };

        List<byte> result = new List<byte>(digits.Count - wholeBytes);
        for (int i = 0; i < digits.Count - wholeBytes; i++)
        {
            int sourceIndex = i + wholeBytes;
            int value = digits[sourceIndex] >> remainderBits;
            if (remainderBits > 0 && sourceIndex + 1 < digits.Count)
                value |= digits[sourceIndex + 1] << (8 - remainderBits);

            result.Add((byte)(value & 0xFF));
        }

        NormalizeInPlace(result);
        return result;
    }

    private static void SubtractProductInPlace(List<byte> current, List<byte> divisor, byte factor)
    {
        int borrow = 0;
        for (int i = 0; i < current.Count; i++)
        {
            int productDigit = (i < divisor.Count ? divisor[i] : 0) * factor + borrow;
            int diff = current[i] - (productDigit & 0xFF);
            if (diff < 0)
            {
                diff += 0x100;
                borrow = (productDigit >> 8) + 1;
            }
            else
            {
                borrow = productDigit >> 8;
            }

            current[i] = (byte)diff;
        }

        NormalizeInPlace(current);
    }

    private static byte BinarySearchDivide(List<byte> dividend, List<byte> divisor)
    {
        int low = 0;
        int high = 0xFF;

        while (low <= high)
        {
            int mid = low + ((high - low) >> 1);
            var product = MultiplyBytesByUInt(divisor, (uint)mid);
            int comparison = CompareAbsolute(product, dividend);

            if (comparison <= 0)
            {
                if (mid == 0xFF)
                    return (byte)mid;

                var nextProduct = MultiplyBytesByUInt(divisor, (uint)(mid + 1));
                if (CompareAbsolute(nextProduct, dividend) > 0)
                    return (byte)mid;

                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return 0;
    }

    private static void DivideUnsigned(List<byte> dividend, List<byte> divisor, out List<byte> quotient, out List<byte> remainder)
    {
        quotient = new List<byte> { 0 };
        remainder = new List<byte> { 0 };

        if (IsZero(divisor))
            throw new DivideByZeroException();
        if (IsZero(dividend))
            return;

        int comparison = CompareAbsolute(dividend, divisor);
        if (comparison < 0)
        {
            remainder = new List<byte>(dividend);
            return;
        }
        if (comparison == 0)
        {
            quotient = new List<byte> { 1 };
            return;
        }

        uint smallDivisor;
        if (TryToUInt32(divisor, out smallDivisor))
        {
            uint smallRemainder;
            quotient = DivideBytesByUInt(dividend, smallDivisor, out smallRemainder);
            remainder = FromUInt32(smallRemainder);
            return;
        }

        quotient = new List<byte>(dividend.Count);
        List<byte> current = new List<byte>(divisor.Count + 2);

        for (int i = dividend.Count - 1; i >= 0; i--)
        {
            current.Insert(0, dividend[i]);
            NormalizeInPlace(current);

            if (CompareAbsolute(current, divisor) < 0)
            {
                quotient.Insert(0, (byte)0);
                continue;
            }

            byte quotientDigit = BinarySearchDivide(current, divisor);
            quotient.Insert(0, quotientDigit);
            SubtractProductInPlace(current, divisor, quotientDigit);
        }

        remainder = current;
        NormalizeInPlace(quotient);
    }

    private static HugeInt Divide(HugeInt dividend, HugeInt divisor, out HugeInt remainder)
    {
        remainder = Zero;

        List<byte> dividendDigits = GetDigits(dividend);
        List<byte> divisorDigits = GetDigits(divisor);
        bool dividendNegative = IsNegativeValue(dividend);
        bool divisorNegative = IsNegativeValue(divisor);

        List<byte> absDividend = dividendNegative ? new List<byte>(dividendDigits) : dividendDigits;
        List<byte> absDivisor = divisorNegative ? new List<byte>(divisorDigits) : divisorDigits;

        DivideUnsigned(absDividend, absDivisor, out List<byte> quotientDigits, out List<byte> remainderDigits);

        bool resultNegative = dividendNegative != divisorNegative;
        remainder = new HugeInt(remainderDigits, dividendNegative);
        return new HugeInt(quotientDigits, resultNegative);
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
        bool leftNegative = IsNegativeValue(this);
        bool rightNegative = IsNegativeValue(other);

        if (leftNegative != rightNegative)
            return leftNegative ? -1 : 1;

        int absoluteComparison = CompareAbsolute(GetDigits(this), GetDigits(other));
        return leftNegative ? -absoluteComparison : absoluteComparison;
    }

    public bool Equals(HugeInt other)
    {
        return IsNegativeValue(this) == IsNegativeValue(other) &&
               CompareAbsolute(GetDigits(this), GetDigits(other)) == 0;
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
            foreach (byte digit in GetDigits(this))
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

    /// <summary>
    /// 写入已有 StringBuilder。ToString 的高频路径：
    /// 小数字直接走 ulong 快路径；大数字用 10^9 原地长除，每次取出 9 位十进制数，
    /// 过程中只复制一份字节表，不会反复分配 HugeInt。
    /// </summary>
    public void ToStringBuilder(StringBuilder sb)
    {
        if (sb == null)
            throw new ArgumentNullException(nameof(sb));

        List<byte> digits = GetDigits(this);
        if (IsZero(digits))
        {
            sb.Append('0');
            return;
        }

        bool negative = IsNegativeValue(this);
        if (digits.Count <= 8)
        {
            if (negative) sb.Append('-');
            sb.Append(ToUInt64Magnitude(digits));
            return;
        }

        List<byte> current = new List<byte>(digits);
        int estimatedGroups = (int)Math.Min(int.MaxValue, ((long)digits.Count * 27) / 100 + 2);
        List<uint> remainders = new List<uint>(Math.Max(1, estimatedGroups));

        while (!IsZero(current))
            remainders.Add(DivideBytesByUIntInPlace(current, 1000000000u));

        if (negative) sb.Append('-');

        for (int i = remainders.Count - 1; i >= 0; i--)
            AppendUIntGroup(sb, remainders[i], i == remainders.Count - 1 ? 0 : 9);
    }

    /// <summary>将 uint v 转为十进制写入 sb，minDigits > 0 时不足补前导零。</summary>
    private static void AppendUIntGroup(StringBuilder sb, uint v, int minDigits)
    {
        int start = sb.Length;
        if (v == 0 && minDigits <= 1)
        {
            sb.Append('0');
            return;
        }

        while (v > 0)
        {
            sb.Append((char)('0' + v % 10));
            v /= 10;
        }

        while (sb.Length - start < minDigits)
            sb.Append('0');

        int end = sb.Length - 1;
        while (start < end)
        {
            char t = sb[start];
            sb[start] = sb[end];
            sb[end] = t;
            start++;
            end--;
        }
    }

    public string ToString(string format, IFormatProvider formatProvider)
    {
        return ToString();
    }
    #endregion

    #region 实用方法与常量
    private static ulong ToUInt64Magnitude(List<byte> digits)
    {
        ulong value = 0;
        for (int i = digits.Count - 1; i >= 0; i--)
            value = (value << 8) | digits[i];

        return value;
    }

    public long ToLong()
    {
        List<byte> digits = GetDigits(this);
        bool negative = IsNegativeValue(this);

        if (digits.Count > 8)
            throw new OverflowException("Value is too large for long");

        ulong magnitude = ToUInt64Magnitude(digits);
        if (negative)
        {
            if (magnitude > 0x8000000000000000UL)
                throw new OverflowException("Value is too small for long");
            return magnitude == 0x8000000000000000UL ? long.MinValue : -(long)magnitude;
        }

        if (magnitude > (ulong)long.MaxValue)
            throw new OverflowException("Value is too large for long");
        return (long)magnitude;
    }

    public float ToFloat()
    {
        List<byte> digits = GetDigits(this);
        if (IsZero(digits))
            return 0f;

        bool negative = IsNegativeValue(this);
        double value;

        if (digits.Count <= 8)
        {
            // 可完整放入 ulong：直接转 double，普通量级能得到精确的 float
            value = ToUInt64Magnitude(digits);
        }
        else
        {
            // 只取最高 8 字节做尾数，再按 256 的幂缩放；double 有 53 位精度，对 float 足够
            int topIndex = digits.Count - 1;
            int taken = Math.Min(8, digits.Count);
            double mantissa = 0;
            for (int i = 0; i < taken; i++)
                mantissa = mantissa * 256.0 + digits[topIndex - i];

            int scaleBytes = topIndex - (taken - 1);
            value = mantissa * System.Math.Pow(256.0, scaleBytes);
        }

        return (float)(negative ? -value : value);
    }

    public static HugeInt Zero { get; } = new HugeInt(0);
    public static HugeInt One { get; } = new HugeInt(1);
    public static HugeInt Ten { get; } = new HugeInt(10);

    private static readonly HugeInt Thousand = (HugeInt)1000;
    private static readonly HugeInt Million = (HugeInt)1000000;
    private static readonly HugeInt Billion = (HugeInt)1000000000;
    private static readonly HugeInt Trillion = (HugeInt)1000000000000;
    private static readonly HugeInt Quadrillion = (HugeInt)1000000000000000;

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

            exponent >>= 1;
            if (exponent > 0)
                baseValue *= baseValue;
        }

        return result;
    }

    /// <summary>与 Log2 等价（本项目大数对数统一以 2 为底）。O(1)，高频调用也不产生分配。</summary>
    public static float Log(HugeInt value) => Log2(value);

    /// <summary>
    /// 计算以 2 为底的对数（浮点）。O(1)。
    /// 取最高 4 个字节组成整数 mantissa，value ≈ mantissa * 256^scaleBytes，
    /// 因此 log2(value) = scaleBytes * 8 + log2(mantissa)，精度远高于只取最高 1 字节。
    /// </summary>
    public static float Log2(HugeInt value)
    {
        List<byte> digits = GetDigits(value);
        if (IsZero(digits) || IsNegativeValue(value))
            throw new ArgumentException("Value must be positive");

        int topIndex = digits.Count - 1;
        int taken = Math.Min(4, digits.Count);
        double mantissa = 0;
        for (int i = 0; i < taken; i++)
            mantissa = mantissa * 256.0 + digits[topIndex - i];

        int scaleBytes = topIndex - (taken - 1);
        return (float)(scaleBytes * 8.0 + System.Math.Log(mantissa, 2.0));
    }

    // 自动保留一位小数（如 1.2K、9.9M）
    public string ToShortString() => ToShortString(false);
    // 指定是否省略小数点
    public string ToShortString(bool nopoint)
    {
        HugeInt absValue = IsNegativeValue(this) ? -this : this;   // 只显示绝对值
        if (absValue < Thousand)
            return absValue.ToString();

        HugeInt baseValue;
        string suffix;
        if (absValue < Million) { baseValue = Thousand; suffix = "K"; }
        else if (absValue < Billion) { baseValue = Million; suffix = "M"; }
        else if (absValue < Trillion) { baseValue = Billion; suffix = "B"; }
        else if (absValue < Quadrillion) { baseValue = Trillion; suffix = "T"; }
        else { baseValue = Quadrillion; suffix = "P"; }

        // 大于等于 10*基数 或要求不显示小数 → 直接取整
        if (nopoint || absValue >= baseValue * Ten)
            return (absValue / baseValue).ToString() + suffix;

        // 保留一位小数：计算 absValue / (基数/10) 并在倒数第二位前插入小数点
        HugeInt scaled = absValue / (baseValue / Ten);
        string scaledStr = scaled.ToString();
        return scaledStr.Insert(scaledStr.Length - 1, ".") + suffix;
    }
    #endregion
}
