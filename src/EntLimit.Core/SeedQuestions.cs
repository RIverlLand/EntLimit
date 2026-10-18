namespace EntLimit.Core;

/// <summary>Original, offline exercises. SourceUrl credits factual references, not copied questions.</summary>
public static class SeedQuestions
{
    private const string PyData = "https://docs.python.org/3/tutorial/datastructures.html";
    private const string PyZip = "https://docs.python.org/3/library/functions.html#zip";
    private const string Ip = "https://docs.python.org/3/library/ipaddress.html";
    private const string PrivateIp = "https://www.rfc-editor.org/info/rfc1918/";
    private const string Dns = "https://developers.cloudflare.com/learning-paths/cybersafe/concepts/what-is-dns/";
    private const string Layers = "https://developers.cloudflare.com/fundamentals/reference/network-layers/";
    private const string Vlan = "https://www.cisco.com/en/US/docs/ios/12_4/lan_switch/configuration/guide/confvlan.html";

    public static IReadOnlyList<Question> All { get; } = Array.AsReadOnly(new[]
    {
        Q("py-light-alias", "python", "light", "三个列表，还是同一个列表？", 2,
            "阅读代码：\na = [[]] * 3\na[0].append(1)\n\n执行后 a 的值是什么？先想想 * 3 重复的究竟是什么。",
            ["[[1], [], []]", "[[1], [1], [1]]", "[1, 1, 1]", "抛出 TypeError"], 1,
            "外层列表中的三个元素引用同一个内层列表，所以修改一次会在三个位置看到结果。需要独立列表时使用 [[] for _ in range(3)]。",
            "列表重复复制的是元素引用；创建独立可变对象可以用推导式。", PyData),
        Q("py-light-zip", "python", "light", "长度不同，zip 会怎样？", 2,
            "list(zip([10, 20, 30], ['a', 'b'])) 会产生几个配对？如果业务要求两个输入长度相同，哪项是合适的做法？",
            ["默认三个配对，缺少的补 None", "默认两个配对；zip(..., strict=True) 可检查长度不一致", "默认直接抛异常", "默认两个配对，strict=True 会补齐第三个"], 1,
            "默认 zip 在最短输入耗尽时结束，结果为 [(10, 'a'), (20, 'b')]。strict=True 会在输入长度不一致时抛出 ValueError，防止静默丢数据。",
            "把“默认截断”和“业务要求严格匹配”区分开。", PyZip),
        Q("py-light-dict", "python", "light", "字典中重复的键", 2,
            "d = {'a': 1, 'b': 2}\nd['a'] = 3\n\nlist(d.items()) 的结果是什么？",
            ["[('b', 2), ('a', 3)]", "[('a', 1), ('b', 2), ('a', 3)]", "[('a', 3), ('b', 2)]", "抛出重复键异常"], 2,
            "字典中的键唯一。更新已有键只替换值，不把键移到末尾；插入顺序仍然是 a、b。",
            "字典更新值时保留已有键的位置。", PyData),
        Q("py-standard-dedupe", "python", "standard", "有顺序的去重", 5,
            "需求：对整数列表去重，保留每个值第一次出现的顺序。例：[3, 1, 3, 2, 1] → [3, 1, 2]。\n\n先自己写一个版本，再检查空列表、负数、全重复输入。下面哪种写法符合要求？",
            ["list(set(values))", "sorted(set(values))", "list(dict.fromkeys(values))", "[v for v in values if values.count(v) == 1]"], 2,
            "dict 保持插入顺序，fromkeys 每个值只保留一个键，因此 list(dict.fromkeys(values)) 保留首次出现顺序。set 不承诺此顺序，排序改变顺序，最后一项会把所有重复值整个删除。这里输入限定为整数，能够作为字典键。",
            "先明确顺序和重复的业务语义，再选择数据结构。", PyData),
        Q("py-standard-count", "python", "standard", "统计状态码，不丢掉第一次", 5,
            "实现 count_codes(codes)，返回每个整数状态码的出现次数。\n例：[200, 404, 200] → {200: 2, 404: 1}\n\n先写函数并用 []、[500]、[200, 200, 200] 检查。对于循环中的每个 code，下列哪一行正确更新 result？",
            ["result[code] = result.get(code, 0) + 1", "result[code] = 1", "result[code] += 1（无需初始化）", "result[code] = len(codes)"], 0,
            "get(code, 0) 在键缺失时提供零；加一后保存。空列表返回空字典。只赋 1 会覆盖计数，未初始化直接 += 会在第一次遇到键时抛出 KeyError。",
            "字典累计计数时同时处理首次出现和再次出现。", PyData),
        Q("py-standard-groups", "python", "standard", "分组时意外共享的列表", 6,
            "需求：rows = [('red', 1), ('blue', 2), ('red', 3)]，结果应为 {'red': [1, 3], 'blue': [2]}。\n\n先写一个分组函数。下面哪种初始化和更新不会使所有组共享同一列表？",
            ["先 groups = dict.fromkeys(keys, [])，再 groups[k].append(v)", "每次循环 groups[k] = [v]", "每次循环 groups.setdefault(k, []).append(v)", "先 shared = []，每个新键都设为 shared"], 2,
            "setdefault 在键不存在时为该组创建一个新列表，存在时返回已有列表。dict.fromkeys(keys, []) 的所有键共享同一个列表；每次赋 [v] 则丢掉组中原来的数据。",
            "按组保存可变容器时，每个组需要独立容器。", PyData),
        Q("net-light-dns", "network", "light", "DNS 帮浏览器做了哪一步？", 2,
            "输入 www.example.com 后，DNS 在典型网页访问流程中的作用最接近哪一项？",
            ["直接把完整网页传给浏览器", "查询域名对应的地址等记录", "保证网页内容不会被篡改", "把浏览器进程变成服务器"], 1,
            "DNS 用来查询域名的记录，例如 IPv4/IPv6 地址。拿到地址后，浏览器还需要通过网络连接请求网页。DNS 查询、网页传输和 HTTPS 加密承担不同职责。",
            "域名解析与网页请求是不同步骤。", Dns),
        Q("net-light-private", "network", "light", "两个家庭，同一个地址", 2,
            "小明家的一台电脑地址是 192.168.1.20，小红家也有一台电脑使用 192.168.1.20。两家是独立家庭网络。仅凭这个信息，能断定发生了地址冲突吗？",
            ["能，因为全世界的 IP 都必须唯一", "不能，独立网络可以重复使用私有地址", "能，因为最后一段数字必须是 1", "不能，因为这两个地址是域名"], 1,
            "192.168.0.0/16 是私有 IPv4 地址范围之一。私有地址可在彼此独立的网络中重复使用；同一连接网络中的地址配置则需要避免冲突。IP 地址不是设备永久身份。",
            "私有地址的唯一性要结合所属网络来理解。", PrivateIp),
        Q("net-light-prefix", "network", "light", "/24 告诉我们什么？", 3,
            "IPv4 地址是 32 位。前缀 /24 表示前 24 位作为网络前缀。对 192.168.10.12/24，哪项理解正确？",
            ["24 表示只能连接 24 台设备", "前 24 位确定网络前缀，剩余 8 位区分该子网中的地址", "24 表示 TCP 端口", "最后一段必须等于 24"], 1,
            "IPv4 的每个十进制段对应 8 位，/24 因而固定前三段。它不是端口号或设备数量。设备可用地址还取决于网络和广播地址等规则。",
            "IP 地址和子网前缀共同描述地址所属网络。", Ip),
        Q("net-standard-subnet", "network", "standard", "哪些地址在同一子网？", 5,
            "学习卡：IPv4 是 32 位，/24 固定前 24 位，也就是前三个十进制段。192.168.10.0/24 覆盖 192.168.10.0 至 192.168.10.255。\n\n请先自己举两个同子网和一个不同子网的地址。下面哪组中的三个地址都位于 192.168.10.0/24？",
            ["192.168.10.12、192.168.10.80、192.168.11.12", "192.168.10.12、192.168.10.80、192.168.10.200", "192.168.1.12、192.168.10.80、192.168.10.200", "192.168.10.12、192.168.11.80、192.168.12.200"], 1,
            "/24 固定 192.168.10，最后一段可以变化。注意属于地址范围与适合分配给普通主机是不同问题：常见 /24 下 .0 为网络地址，.255 为广播地址。",
            "用前缀判断子网；范围成员与可分配主机地址要区分。", Ip),
        Q("net-standard-layers", "network", "standard", "一次请求，四种分工", 6,
            "学习卡：HTTP 表达网页请求/响应；TCP 提供可靠有序的字节传输；IP 负责跨网络寻址与转发；以太网处理链路上的帧。\n\n不用背七层名字，先用自己的话描述一次网页请求。下面哪组 OSI 层次配对符合这张卡片？",
            ["HTTP—应用层；TCP—传输层；IP—网络层；以太网—数据链路层", "HTTP—网络层；TCP—应用层；IP—传输层；以太网—应用层", "HTTP、TCP、IP、以太网全是应用层", "HTTP—传输层；TCP—数据链路层；IP—应用层；以太网—网络层"], 0,
            "不同协议处理不同问题。OSI 是帮助描述分工的模型，TCP/IP 模型的层数和名称有所不同。此处使用 OSI 名称；现实中 HTTP/3 可使用 QUIC，不应把所有 HTTP 都等同于 TCP。",
            "网络分层是职责划分；看协议在解决哪个问题。", Layers),
        Q("net-standard-vlan", "network", "standard", "同一交换机，也能分开", 6,
            "学习卡：VLAN 把交换网络划分为不同的逻辑广播域。访客和员工可以连接同一台支持 VLAN 的交换机，却属于不同 VLAN。跨 VLAN 通信需要相应的路由和策略配置。\n\n下面哪句话正确？",
            ["同一交换机上的设备必然在同一 VLAN", "VLAN 只是 Wi-Fi 密码的新名称", "VLAN 可划分逻辑广播域；跨 VLAN 不会仅因共用交换机就自动互通", "只要用了 VLAN，所有应用数据就自动加密"], 2,
            "VLAN 是链路层的逻辑分组，不是密码或加密机制。支持三层功能的交换机可以执行跨 VLAN 路由，但这仍需要相应配置。隔离效果还依赖具体网络设计和策略。",
            "VLAN 负责逻辑分组；路由、访问控制和加密是另外的职责。", Vlan)
    });

    private static Question Q(string id, string topic, string mode, string title, int minutes,
        string prompt, string[] options, int correct, string explanation, string takeaway, string source) => new()
    {
        Id = id, TopicId = topic, Mode = mode, Title = title, EstimatedMinutes = minutes,
        Prompt = prompt, Options = options, CorrectIndex = correct, Explanation = explanation,
        Takeaway = takeaway, SourceUrl = source
    };
}
