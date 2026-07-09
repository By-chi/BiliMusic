extends Node

# 综合排序等映射
const ORDER_MAP = {
	0: "totalrank",
	1: "click",
	2: "pubdate",
	3: "dm",
	4: "stow",
	5: "scores"
}

const CACHE_DIR := "user://bilibili_cover_cache/"
const MAX_CACHE_SIZE := 1024
const CACHE_LOAD_COOLDOWN_MS: int = 50
const CACHE_QUEUE_MAX_SIZE: int = 40

static var _cached_buvid: String = ""
# cookie 字段生成与缓存
static func _get_or_generate_cookie_field(key: String, generator: Callable) -> String:
	var value = GdScriptFunc.get_data("Network", key, "")
	if value.is_empty():
		value = generator.call()
		GdScriptFunc.set_data("Network", key, value)
	return value

static func _random_string(length: int = 16) -> String:
	const CHARS = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"
	var result = ""
	for i in range(length):
		result += CHARS[randi() % CHARS.length()]
	return result

static func _generate_buvid4() -> String:
	# 格式：UUID + 时间戳 + 随机后缀（模仿真实）
	var uuid = "%04x%04x-%04x-%04x-%04x-%04x%04x%04x" % [
		randi() % 0xFFFF, randi() % 0xFFFF,
		randi() % 0xFFFF, (randi() % 0xFFFF) | 0x4000,
		(randi() % 0xFFFF) | 0x8000,
		randi() % 0xFFFF, randi() % 0xFFFF, randi() % 0xFFFF
	]
	var timestamp = str(Time.get_unix_time_from_system())
	var suffix = _random_string(20)
	return "%s-%s-%s" % [uuid, timestamp, suffix]

static func _generate_fingerprint() -> String:
	# 模仿真实指纹：随机MD5
	return _random_string(32).md5_text()

static func _generate_rpdid() -> String:
	# 格式：随机字符，长度30左右
	return _random_string(30)

static func _generate_b_lsid() -> String:
	# 格式：类似 "E9D811FC_19F45923A16"
	return _random_string(8).to_upper() + "_" + _random_string(12).to_upper()
# 原始无参版本（保留用于其他接口）
func _get_headers() -> PackedStringArray:
	return _get_headers_with_mid(0)

# 新增有参版本
func _get_headers_with_mid(mid: int = 0) -> PackedStringArray:
	var cookies = [
		"buvid3=" + get_or_generate_buvid(),
		"buvid4=" + _get_or_generate_cookie_field("buvid4", _generate_buvid4),
		"b_nut=" + generate_fake_b_nut(),
		"rpdid=" + _get_or_generate_cookie_field("rpdid", _generate_rpdid),
		"_uuid=" + _get_or_generate_cookie_field("_uuid", func(): return _random_string(8).to_upper() + "-" + _random_string(4) + "-" + _random_string(4) + "-" + _random_string(4) + "-" + _random_string(12).to_upper() + "infoc"),
		"theme-tip-show=SHOWED",
		"theme-avatar-tip-show=SHOWED",
		"theme-switch-show=SHOWED",
		"theme_style=dark",
		"hit-dyn-v2=1",
		"buvid_fp_plain=undefined",
		"LIVE_BUVID=AUTO" + str(Time.get_unix_time_from_system()) + "411",
		"fingerprint=" + _get_or_generate_cookie_field("fingerprint", _generate_fingerprint),
		"buvid_fp=" + _get_or_generate_cookie_field("buvid_fp", _generate_fingerprint),
		"PVID=1",
		"ogv_device_support_dolby=0",
		"ogv_device_support_hdr=0",
		"browser_resolution=" + str(DisplayServer.screen_get_size().x) + "-" + str(DisplayServer.screen_get_size().y),
		"home_feed_column=4",
		"b_lsid=" + _get_or_generate_cookie_field("b_lsid", _generate_b_lsid)
	]

	# 登录相关 cookie（动态获取）
	var sess = GdScriptFunc.get_data("AccountData", "SESSDATA")
	if sess != null and sess != "":
		cookies.append("SESSDATA=" + sess)

	var bili_jct = GdScriptFunc.get_data("AccountData", "bili_jct")
	if bili_jct != null and bili_jct != "":
		cookies.append("bili_jct=" + bili_jct)

	var dedeuserid = GdScriptFunc.get_data("AccountData", "DedeUserID")
	if dedeuserid != null and dedeuserid != "":
		cookies.append("DedeUserID=" + dedeuserid)

	var dedeuserid_ckmd5 = GdScriptFunc.get_data("AccountData", "DedeUserID__ckMd5")
	if dedeuserid_ckmd5 != null and dedeuserid_ckmd5 != "":
		cookies.append("DedeUserID__ckMd5=" + dedeuserid_ckmd5)

	var sid = GdScriptFunc.get_data("AccountData", "sid")
	if sid != null and sid != "":
		cookies.append("sid=" + sid)

	var bp_offset = GdScriptFunc.get_data("AccountData", "bp_t_offset")
	if bp_offset != null and bp_offset != "":
		cookies.append("bp_t_offset_" + dedeuserid + "=" + bp_offset)

	var cookie = "; ".join(cookies) + ";"

	# 动态 Referer
	var referer = "https://space.bilibili.com/"
	if mid != 0:
		referer += str(mid) + "/upload/video"

	return [
		"User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
		"Referer: " + referer,
		"Origin: https://space.bilibili.com",
		"Accept: application/json, text/plain, */*",
		"Accept-Language: zh-CN,zh;q=0.9,en;q=0.8",
		"Accept-Encoding: gzip, deflate, br",
		"Sec-Ch-Ua: \"Not;A=Brand\";v=\"8\", \"Chromium\";v=\"120\", \"Microsoft Edge\";v=\"120\"",
		"Sec-Ch-Ua-Mobile: ?0",
		"Sec-Ch-Ua-Platform: \"Windows\"",
		"Sec-Fetch-Dest: empty",
		"Sec-Fetch-Mode: cors",
		"Sec-Fetch-Site: same-site",
		"Dnt: 1",
		"Priority: u=1, i",
		"Cookie: " + cookie
	]
func get_csrf() -> String:
	return GdScriptFunc.get_data("AccountData", "bili_jct", "")
# 封面下载专用头，轻量
func _get_image_headers() -> PackedStringArray:
	return [
		"User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
		"Referer: https://www.bilibili.com"
	]
# 静态工具方法
static func generate_fake_b_nut() -> String:
	return str(Time.get_unix_time_from_system())

# 基于系统指纹生成 buvid
static func generate_fingerprint_buvid() -> String:
	var screen_size = DisplayServer.screen_get_size()
	var info = [
		OS.get_name(),
		str(OS.get_processor_count()),
		str(screen_size.x),
		str(screen_size.y),
		OS.get_locale(),
		"GodotEngine/" + Engine.get_version_info().string,
		DisplayServer.get_name()
	]
	var fingerprint = "||".join(info)
	var h = fingerprint.md5_text().to_upper()
	var uuid = h.substr(0, 8) + "-" + \
			   h.substr(8, 4) + "-" + \
			   h.substr(12, 4) + "-" + \
			   h.substr(16, 4) + "-" + \
			   h.substr(20, 12)
	return uuid + "infoc"

# 优先从本地存储读，没有则生成
static func get_or_generate_buvid() -> String:
	if not _cached_buvid.is_empty():
		return _cached_buvid

	_cached_buvid = GdScriptFunc.get_data("Network", "buvid3", "")
	if _cached_buvid != "":
		return _cached_buvid

	_cached_buvid = generate_fingerprint_buvid()
	GdScriptFunc.set_data("Network", "buvid3", _cached_buvid)
	return _cached_buvid

# 处理 HTML 实体，包括命名实体、十进制和十六进制
static func decode_html_entities(text: String) -> String:
	var result = text
	var named_entities = {
		"&amp;": "&",
		"&lt;": "<",
		"&gt;": ">",
		"&quot;": "\"",
		"&apos;": "'",
		"&nbsp;": " ",
		"&iexcl;": "¡",
		"&cent;": "¢",
		"&pound;": "£",
		"&curren;": "¤",
		"&yen;": "¥",
		"&brvbar;": "¦",
		"&sect;": "§",
		"&uml;": "¨",
		"&copy;": "©",
		"&ordf;": "ª",
		"&laquo;": "«",
		"&not;": "¬",
		"&shy;": "\u00AD",
		"&reg;": "®",
		"&macr;": "¯",
		"&deg;": "°",
		"&plusmn;": "±",
		"&sup2;": "²",
		"&sup3;": "³",
		"&acute;": "´",
		"&micro;": "µ",
		"&para;": "¶",
		"&middot;": "·",
		"&cedil;": "¸",
		"&sup1;": "¹",
		"&ordm;": "º",
		"&raquo;": "»",
		"&frac14;": "¼",
		"&frac12;": "½",
		"&frac34;": "¾",
		"&iquest;": "¿",
		"&Agrave;": "À",
		"&Aacute;": "Á",
		"&Acirc;": "Â",
		"&Atilde;": "Ã",
		"&Auml;": "Ä",
		"&Aring;": "Å",
		"&AElig;": "Æ",
		"&Ccedil;": "Ç",
		"&Egrave;": "È",
		"&Eacute;": "É",
		"&Ecirc;": "Ê",
		"&Euml;": "Ë",
		"&Igrave;": "Ì",
		"&Iacute;": "Í",
		"&Icirc;": "Î",
		"&Iuml;": "Ï",
		"&ETH;": "Ð",
		"&Ntilde;": "Ñ",
		"&Ograve;": "Ò",
		"&Oacute;": "Ó",
		"&Ocirc;": "Ô",
		"&Otilde;": "Õ",
		"&Ouml;": "Ö",
		"&times;": "×",
		"&Oslash;": "Ø",
		"&Ugrave;": "Ù",
		"&Uacute;": "Ú",
		"&Ucirc;": "Û",
		"&Uuml;": "Ü",
		"&Yacute;": "Ý",
		"&THORN;": "Þ",
		"&szlig;": "ß",
		"&agrave;": "à",
		"&aacute;": "á",
		"&acirc;": "â",
		"&atilde;": "ã",
		"&auml;": "ä",
		"&aring;": "å",
		"&aelig;": "æ",
		"&ccedil;": "ç",
		"&egrave;": "è",
		"&eacute;": "é",
		"&ecirc;": "ê",
		"&euml;": "ë",
		"&igrave;": "ì",
		"&iacute;": "í",
		"&icirc;": "î",
		"&iuml;": "ï",
		"&eth;": "ð",
		"&ntilde;": "ñ",
		"&ograve;": "ò",
		"&oacute;": "ó",
		"&ocirc;": "ô",
		"&otilde;": "õ",
		"&ouml;": "ö",
		"&divide;": "÷",
		"&oslash;": "ø",
		"&ugrave;": "ù",
		"&uacute;": "ú",
		"&ucirc;": "û",
		"&uuml;": "ü",
		"&yacute;": "ý",
		"&thorn;": "þ",
		"&yuml;": "ÿ",
		"&Alpha;": "Α",
		"&Beta;": "Β",
		"&Gamma;": "Γ",
		"&Delta;": "Δ",
		"&Epsilon;": "Ε",
		"&Zeta;": "Ζ",
		"&Eta;": "Η",
		"&Theta;": "Θ",
		"&Iota;": "Ι",
		"&Kappa;": "Κ",
		"&Lambda;": "Λ",
		"&Mu;": "Μ",
		"&Nu;": "Ν",
		"&Xi;": "Ξ",
		"&Omicron;": "Ο",
		"&Pi;": "Π",
		"&Rho;": "Ρ",
		"&Sigma;": "Σ",
		"&Tau;": "Τ",
		"&Upsilon;": "Υ",
		"&Phi;": "Φ",
		"&Chi;": "Χ",
		"&Psi;": "Ψ",
		"&Omega;": "Ω",
		"&alpha;": "α",
		"&beta;": "β",
		"&gamma;": "γ",
		"&delta;": "δ",
		"&epsilon;": "ε",
		"&zeta;": "ζ",
		"&eta;": "η",
		"&theta;": "θ",
		"&iota;": "ι",
		"&kappa;": "κ",
		"&lambda;": "λ",
		"&mu;": "μ",
		"&nu;": "ν",
		"&xi;": "ξ",
		"&omicron;": "ο",
		"&pi;": "π",
		"&rho;": "ρ",
		"&sigmaf;": "ς",
		"&sigma;": "σ",
		"&tau;": "τ",
		"&upsilon;": "υ",
		"&phi;": "φ",
		"&chi;": "χ",
		"&psi;": "ψ",
		"&omega;": "ω",
		"&thetasym;": "ϑ",
		"&upsih;": "ϒ",
		"&piv;": "ϖ",
		"&bull;": "•",
		"&hellip;": "…",
		"&prime;": "′",
		"&Prime;": "″",
		"&oline;": "‾",
		"&frasl;": "⁄",
		"&weierp;": "℘",
		"&image;": "ℑ",
		"&real;": "ℜ",
		"&trade;": "™",
		"&alefsym;": "ℵ",
		"&larr;": "←",
		"&uarr;": "↑",
		"&rarr;": "→",
		"&darr;": "↓",
		"&harr;": "↔",
		"&crarr;": "↵",
		"&lArr;": "⇐",
		"&uArr;": "⇑",
		"&rArr;": "⇒",
		"&dArr;": "⇓",
		"&hArr;": "⇔",
		"&forall;": "∀",
		"&part;": "∂",
		"&exist;": "∃",
		"&empty;": "∅",
		"&nabla;": "∇",
		"&isin;": "∈",
		"&notin;": "∉",
		"&ni;": "∋",
		"&prod;": "∏",
		"&sum;": "∑",
		"&minus;": "−",
		"&lowast;": "∗",
		"&radic;": "√",
		"&prop;": "∝",
		"&infin;": "∞",
		"&ang;": "∠",
		"&and;": "∧",
		"&or;": "∨",
		"&cap;": "∩",
		"&cup;": "∪",
		"&int;": "∫",
		"&there4;": "∴",
		"&sim;": "∼",
		"&cong;": "≅",
		"&asymp;": "≈",
		"&ne;": "≠",
		"&equiv;": "≡",
		"&le;": "≤",
		"&ge;": "≥",
		"&sub;": "⊂",
		"&sup;": "⊃",
		"&nsub;": "⊄",
		"&sube;": "⊆",
		"&supe;": "⊇",
		"&oplus;": "⊕",
		"&otimes;": "⊗",
		"&perp;": "⊥",
		"&sdot;": "⋅",
		"&lceil;": "⌈",
		"&rceil;": "⌉",
		"&lfloor;": "⌊",
		"&rfloor;": "⌋",
		"&lang;": "⟨",
		"&rang;": "⟩",
		"&loz;": "◊",
		"&spades;": "♠",
		"&clubs;": "♣",
		"&hearts;": "♥",
		"&diams;": "♦",
		"&euro;": "€",          # 欧元符号，HTML 4 无但在实际中常见
		"&ndash;": "–",         # 短破折号
		"&mdash;": "—",         # 长破折号
		"&lsquo;": "‘",         # 左单引号
		"&rsquo;": "’",         # 右单引号
		"&ldquo;": "“",         # 左双引号
		"&rdquo;": "”",         # 右双引号
	}
	for entity in named_entities:
		result = result.replace(entity, named_entities[entity])

	var dec_regex = RegEx.new()
	dec_regex.compile("&#(\\d+);")
	var dec_matches = dec_regex.search_all(result)
	for i in range(dec_matches.size() - 1, -1, -1):
		var match = dec_matches[i]
		var code = match.get_string(1).to_int()
		if code > 0:
			result = result.substr(0, match.get_start(0)) + char(code) + result.substr(match.get_end(0))

	var hex_regex = RegEx.new()
	hex_regex.compile("&#x([0-9a-fA-F]+);")
	var hex_matches = hex_regex.search_all(result)
	for i in range(hex_matches.size() - 1, -1, -1):
		var match = hex_matches[i]
		var code = match.get_string(1).hex_to_int()
		if code > 0:
			result = result.substr(0, match.get_start(0)) + char(code) + result.substr(match.get_end(0))

	return result

# 构造缓存键
static func _get_cache_key(link: String, width: int, height: int) -> String:
	return "%s_%dx%d" % [link, width, height]

static func _get_cache_filename(link: String, width: int, height: int) -> String:
	return _get_cache_key(link, width, height).md5_text() + ".jpg"

# 缓存索引（主线程部分）
var _cache_index: Dictionary = {}
var _cache_loaded: bool = false

var _cache_load_queue: Array = []
var _last_load_process_time: int = 0
var _processing_active: bool = false

# 从持久化存储恢复缓存索引
func _load_cache_index() -> void:
	if _cache_loaded:
		return
	var keys = GdScriptFunc.get_keys("CoverCache")
	for key in keys:
		var entry = GdScriptFunc.get_data("CoverCache", key)
		if typeof(entry) == TYPE_DICTIONARY:
			var file = entry.get("file", "")
			var time = entry.get("time", 0)
			if not file.is_empty():
				_cache_index[key] = { "file": file, "time": time }
	_cache_loaded = true

# 将索引写回存储，先清空再全量写入
func _save_cache_index() -> void:
	var old_keys = GdScriptFunc.get_keys("CoverCache")
	for key in old_keys:
		GdScriptFunc.remove_key("CoverCache", key)
	for key in _cache_index:
		var entry: Dictionary = _cache_index[key]
		GdScriptFunc.set_data("CoverCache", key, entry)

# 加入缓存，超出上限时按时间淘汰最旧项
func _add_to_cache(link: String, width: int, height: int, file_path: String) -> void:
	_load_cache_index()
	var key = _get_cache_key(link, width, height)
	var now = Time.get_unix_time_from_system()
	_cache_index[key] = { "file": file_path, "time": now }

	if _cache_index.size() > MAX_CACHE_SIZE:
		var sorted = []
		for k in _cache_index:
			sorted.append({ "key": k, "time": _cache_index[k]["time"] })
		sorted.sort_custom(func(a, b): return a["time"] < b["time"])
		var to_remove = _cache_index.size() - MAX_CACHE_SIZE
		for i in range(to_remove):
			var entry = sorted[i]
			var old_key: String = entry["key"]
			var old_file: String = _cache_index[old_key]["file"]
			var dir = DirAccess.open(CACHE_DIR)
			if dir.file_exists(old_file):
				dir.remove(old_file)
			_cache_index.erase(old_key)
	_save_cache_index()

# 查询缓存，若文件丢失则清除对应记录
func _get_cached_file(link: String, width: int, height: int) -> String:
	_load_cache_index()
	var key = _get_cache_key(link, width, height)
	if not _cache_index.has(key):
		return ""
	var entry: Dictionary = _cache_index[key]
	var file_path = CACHE_DIR + entry["file"]
	if not FileAccess.file_exists(file_path):
		_cache_index.erase(key)
		_save_cache_index()
		return ""
	return file_path

func _update_cache_index(link: String, width: int, height: int, filename: String) -> void:
	_add_to_cache(link, width, height, filename)

# 启动后台保存线程
func _ready() -> void:
	_save_thread = Thread.new()
	_save_thread.start(_save_worker)
	set_process(false)
	if SubtitleCorrection:
		if not SubtitleCorrection.SubtitleProcessed.is_connected(_on_subtitle_processed):
			SubtitleCorrection.SubtitleProcessed.connect(_on_subtitle_processed)
	#fetch_video_info("BV1modgBJEEN",func(info:Dictionary): #测试
		#print(info)
	#)
	#fetch_subtitle_auto("BV1LQRhBnEAr",func(info:Dictionary): #测试
		#printt(info,{})
	#)
	# 如果本地已有 SESSDATA，直接可用
func _exit_tree() -> void:
	_stop_save_thread = true
	_save_semaphore.post()
	if _save_thread and _save_thread.is_alive():
		_save_thread.wait_to_finish()

# 主线程每帧检查加载队列，按冷却时间逐个处理
func _process(_delta: float) -> void:
	var now = Time.get_ticks_msec()
	if not _cache_load_queue.is_empty() and now - _last_load_process_time >= CACHE_LOAD_COOLDOWN_MS:
		_last_load_process_time = now
		_process_one_cache_load_task()
	if _cache_load_queue.is_empty():
		_processing_active = false
		set_process(false)

# 处理一个缓存加载任务，若文件丢失或损坏则重新下载
func _process_one_cache_load_task() -> void:
	if _cache_load_queue.is_empty():
		return
	var task = _cache_load_queue.pop_front()
	var link: String = task["link"]
	var width: int = task["width"]
	var height: int = task["height"]
	var callback: Callable = task["callback"]
	var cached_path: String = task["cached_path"]

	if not FileAccess.file_exists(cached_path):
		push_error("缓存文件丢失，将重新下载 (", link, ")")
		_get_cover_url(link, width, height, func(url):
			if url.is_empty():
				GdScriptFunc.safe_callback(link, null, callback)
				return
			_download_cover(url, link, width, height, callback)
		)
	else:
		var img := Image.new()
		if img.load(cached_path) == OK:
			var texture := ImageTexture.create_from_image(img)
			GdScriptFunc.safe_callback(link, texture, callback)
		else:
			push_error("缓存图片损坏，将重新下载 (", link, ")")
			DirAccess.remove_absolute(cached_path)
			_get_cover_url(link, width, height, func(url):
				if url.is_empty():
					GdScriptFunc.safe_callback(link, null, callback)
					return
				_download_cover(url, link, width, height, callback)
			)

# 立即清空加载队列（队列过长时调用）
func _flush_load_queue_immediate() -> void:
	while not _cache_load_queue.is_empty():
		_process_one_cache_load_task()
	_last_load_process_time = Time.get_ticks_msec()

# 若队列非空且未在处理中，启动帧处理
func _ensure_queue_processing() -> void:
	if _cache_load_queue.is_empty():
		return
	if not _processing_active:
		_processing_active = true
		_last_load_process_time = Time.get_ticks_msec()
		set_process(true)

# 后台保存线程
var _save_thread: Thread = null
var _save_queue: Array = []
var _save_mutex: Mutex = Mutex.new()
var _save_semaphore: Semaphore = Semaphore.new()
var _stop_save_thread: bool = false

func _save_worker() -> void:
	while not _stop_save_thread:
		_save_semaphore.wait()
		if _stop_save_thread:
			break
		_save_mutex.lock()
		if _save_queue.is_empty():
			_save_mutex.unlock()
			continue
		var task = _save_queue.pop_front()
		_save_mutex.unlock()

		var link = task["link"]
		var width = task["width"]
		var height = task["height"]
		var image_data = task["image_data"]
		var filename = _get_cache_filename(link, width, height)
		var file_path = CACHE_DIR + filename

		var dir = DirAccess.open(CACHE_DIR)
		if not dir:
			DirAccess.make_dir_recursive_absolute(CACHE_DIR)

		var file = FileAccess.open(file_path, FileAccess.WRITE)
		if file:
			file.store_buffer(image_data)
			file.close()
			# 回到主线程更新索引
			call_deferred("_update_cache_index", link, width, height, filename)
		else:
			push_error("[后台线程] 无法写入缓存文件: ", file_path)

# 通用 HTTP 请求包装（支持动态 Referer）
func _request(url: String, callback: Callable, extra: Variant = null, method: int = HTTPClient.METHOD_GET, custom_headers: PackedStringArray = _get_headers(), mid: int = 0) -> void:
	var http = HTTPRequest.new()
	add_child(http)

	var headers = custom_headers
	if mid != 0 and custom_headers == _get_headers():
		headers = _get_headers_with_mid(mid)

	var wrapped_callback = func(result: int, response_code: int, resp_headers: PackedStringArray, body: PackedByteArray):
		http.queue_free()
		callback.call(result, response_code, resp_headers, body, extra)

	http.request_completed.connect(wrapped_callback)
	var err = http.request(url, headers, method)
	if err != OK:
		push_error("HTTP请求失败: ", err)
		http.queue_free()
		callback.call(HTTPRequest.RESULT_REQUEST_FAILED, 0, [], PackedByteArray(), extra)
# 搜索，keyword 为"bilibili音乐周榜"时走榜单接口,
# 关于tids有:
# 3,音乐主区(默认)    130,音乐综合    29,音乐现场    59,演奏    31,翻唱    193,MV    30,VOCALOID·UTAU    194,电音    28,原创音乐
func search_bilibili(callback: Callable, keyword: String, num: int = 10, order = 0, page := 1, author: String = "", _tids:=3) -> void:
	
	if keyword == "bilibili音乐周榜":
		_fetch_music_rank_static(callback)
		return
	var order_str: String = ORDER_MAP.get(order, "totalrank") if order is int else order
	var query = {
		"keyword": keyword,
		"page": page,
		"order": order_str,
		"page_size": num,
		"search_type": "video",
	}
	var query_string = ""
	for key in query:
		if not query_string.is_empty():
			query_string += "&"
		query_string += key + "=" + str(query[key]).uri_encode()
	var url = "https://api.bilibili.com/x/web-interface/search/type?" + query_string
	url = await _sign_wbi_url(url)
	_request(url, _on_search_response, [callback, author])

func _on_search_response(_result: int, response_code: int, _headers: PackedStringArray, body: PackedByteArray, extra: Variant) -> void:
	var extra_arr: Array = extra
	var callback: Callable = extra_arr[0]
	var author_filter: String = extra_arr[1] if extra_arr.size() > 1 else ""

	if response_code != 200:
		push_error("搜索请求失败，状态码: ", response_code)
		callback.call([{}])
		return

	var raw = body.get_string_from_utf8()
	if raw.is_empty():
		push_error("搜索响应体为空")
		callback.call([{}])
		return
	if raw.strip_edges().begins_with("<"):
		push_error("搜索被风控拦截或API发生错误，收到HTML响应。原始数据前200字符: ", raw.left(200))
		callback.call([{}])
		return
	var json = JSON.new()
	if json.parse(raw) != OK:
		push_error("JSON解析失败，原始数据前200字符: ", raw.left(200))
		callback.call([{}])
		return

	var data = json.get_data()
	if data.get("code") != 0:
		# 如果code为-352，明确是风控问题
		push_error("API返回错误: code=%d, message=%s" % [data.get("code"), data.get("message")])
		callback.call([{}])
		return

	var videos = []
	for item in data.get("data", {}).get("result", []):
		var bvid = item.get("bvid", "")
		if bvid.is_empty():
			continue
		if author_filter != "" and item.get("author", "") != author_filter:
			continue

		videos.append({
			"link": bvid,
			"BV": bvid,
			"title": decode_html_entities(item.get("title", "").replace('<em class="keyword">', "").replace("</em>", "")),
			"author": decode_html_entities(item.get("author", "")),
			"play": item.get("play", 0),
			"danmaku": item.get("video_review", 0),
			"duration": item.get("duration", ""),
			"description": decode_html_entities(item.get("description", ""))
		})

	callback.call(videos)

# 音乐榜单：先获取最新榜单ID，再拉取歌曲列表
func _fetch_music_rank_static(callback: Callable) -> void:
	var url = "https://api.bilibili.com/x/copyright-music-publicity/toplist/all_period?list_type=1"
	_request(url, _on_all_period_response, [callback])

func _on_all_period_response(result: int, response_code: int, _headers: PackedStringArray, body: PackedByteArray, extra: Variant) -> void:
	var callback: Callable = (extra as Array)[0]
	if result != HTTPRequest.RESULT_SUCCESS or response_code != 200:
		push_error("获取榜单ID失败")
		callback.call([{}])
		return

	var json = JSON.new()
	if json.parse(body.get_string_from_utf8()) != OK:
		push_error("JSON解析失败")
		callback.call([{}])
		return

	var data = json.get_data()
	if data.get("code", -1) != 0:
		push_error("API错误: ", data.get("message", ""))
		callback.call([{}])
		return

	var periods = data.get("data", {}).get("list", {})
	var latest_id = 0
	var latest_time = 0
	for year in periods:
		var list = periods[year]
		if list is Array:
			for period in list:
				var pid = period.get("ID", 0)
				var ptime = period.get("publish_time", 0)
				if ptime > latest_time:
					latest_time = ptime
					latest_id = pid

	if latest_id == 0:
		push_error("未找到任何榜单ID")
		callback.call([{}])
		return

	_fetch_music_list_static(latest_id, callback)

func _fetch_music_list_static(list_id: int, callback: Callable) -> void:
	var url = "https://api.bilibili.com/x/copyright-music-publicity/toplist/music_list?list_id=%d" % list_id
	_request(url, _on_music_list_response, [callback])

func _on_music_list_response(result: int, response_code: int, _headers: PackedStringArray, body: PackedByteArray, extra: Variant) -> void:
	var callback: Callable = (extra as Array)[0]
	if result != HTTPRequest.RESULT_SUCCESS or response_code != 200:
		push_error("获取歌曲列表失败")
		callback.call([{}])
		return

	var json = JSON.new()
	if json.parse(body.get_string_from_utf8()) != OK:
		push_error("JSON解析失败")
		callback.call([{}])
		return

	var data = json.get_data()
	if data.get("code", -1) != 0:
		push_error("API错误: ", data.get("message", ""))
		callback.call([{}])
		return

	var music_data = data.get("data", {})
	var music_list = music_data.get("list", [])
	if not (music_list is Array):
		music_list = []

	var videos = []
	for item in music_list:
		var bvid = item.get("creation_bvid", "")
		if bvid.is_empty():
			bvid = item.get("mv_bvid", "")
			if bvid.is_empty():
				continue

		videos.append({
			"link": bvid,
			"BV": bvid,
			"title": decode_html_entities(item.get("creation_title", "")),
			"author": decode_html_entities(item.get("creation_nickname", "")),
			"description": decode_html_entities(item.get("creation_reason", "")),
			"play": item.get("creation_play", 0),
		})
	callback.call(videos)

# 封面获取：先查缓存，没命中则请求 API 拿到缩略图地址并下载
func fetch_cover(link: String, callback: Callable, width: int = 160, height: int = 160) -> void:
	var cached_path := _get_cached_file(link, width, height)
	if not cached_path.is_empty():
		_cache_load_queue.push_back({
			"link": link,
			"width": width,
			"height": height,
			"callback": callback,
			"cached_path": cached_path
		})
		if _cache_load_queue.size() >= CACHE_QUEUE_MAX_SIZE:
			_flush_load_queue_immediate()
		else:
			_ensure_queue_processing()
		return

	_get_cover_url(link, width, height, func(thumbnail_url: String):
		if thumbnail_url.is_empty():
			GdScriptFunc.safe_callback(link, null, callback)
			return
		_download_cover(thumbnail_url, link, width, height, callback)
	)

func _get_cover_url(bvid: String, width: int, height: int, next: Callable) -> void:
	var url = "https://api.bilibili.com/x/web-interface/view?bvid=" + bvid
	_request(url, _on_cover_url_received, [bvid, width, height, next])

func _on_cover_url_received(_result: int, response_code: int, _headers: PackedStringArray, body: PackedByteArray, extra: Variant) -> void:
	var extra_arr: Array = extra
	var bvid: String = extra_arr[0]
	var width: int = extra_arr[1]
	var height: int = extra_arr[2]
	var next: Callable = extra_arr[3]

	if response_code != 200:
		push_error("获取视频信息失败 (", bvid, "): ", response_code)
		next.call("")
		return

	var json = JSON.new()
	if json.parse(body.get_string_from_utf8()) != OK:
		push_error("JSON解析失败 (", bvid, ")")
		next.call("")
		return

	var data = json.get_data()
	if data.get("code") != 0:
		push_error("API返回错误 (", bvid, "): ", data.get("message"))
		next.call("")
		return

	var original_pic_url = data.get("data", {}).get("pic", "")
	if original_pic_url.is_empty():
		push_error("未找到封面URL (", bvid, ")")
		next.call("")
		return

	var thumbnail_url = original_pic_url + "@" + str(width) + "w_" + str(height) + "h_1c.jpg"
	next.call(thumbnail_url)

func _download_cover(image_url: String, bvid: String, width: int, height: int, callback: Callable) -> void:
	var http = HTTPRequest.new()
	add_child(http)
	http.request_completed.connect(func(_result, response_code, _headers, body):
		http.queue_free()
		if response_code != 200:
			push_error("下载封面失败 (", bvid, "): ", response_code)
			GdScriptFunc.safe_callback(bvid, null, callback)
			return

		var image := Image.new()
		if image.load_jpg_from_buffer(body) != OK and image.load_png_from_buffer(body) != OK:
			push_error("图片数据解析失败 (", bvid, ")")
			GdScriptFunc.safe_callback(bvid, null, callback)
			return
		var texture := ImageTexture.create_from_image(image)
		GdScriptFunc.safe_callback(bvid, texture, callback)

		# 投递到后台保存队列
		_save_mutex.lock()
		_save_queue.push_back({
			"link": bvid,
			"width": width,
			"height": height,
			"image_data": body
		})
		_save_mutex.unlock()
		_save_semaphore.post()
	)

	var err = http.request(image_url, _get_image_headers(), HTTPClient.METHOD_GET)
	if err != OK:
		push_error("封面下载请求失败 (", bvid, "): ", err)
		http.queue_free()
		GdScriptFunc.safe_callback(bvid, null, callback)

# 通过 BV 号获取视频详细信息
func fetch_video_info(bvid: String, callback: Callable) -> void:
	var url = "https://api.bilibili.com/x/web-interface/view?bvid=" + bvid
	_request(url, _on_video_info_response, [bvid, callback])

func _on_video_info_response(_result: int, response_code: int, _headers: PackedStringArray, body: PackedByteArray, extra: Variant) -> void:
	var extra_arr: Array = extra
	var bvid: String = extra_arr[0]
	var callback: Callable = extra_arr[1]
	
	if response_code != 200:
		push_error("获取视频信息失败 (", bvid, "): HTTP ", response_code)
		callback.call({})
		return
		
	var json = JSON.new()
	if json.parse(body.get_string_from_utf8()) != OK:
		push_error("JSON解析失败 (", bvid, ")")
		callback.call({})
		return
		
	var data = json.get_data()
	if data.get("code") != 0:
		push_error("API返回错误 (", bvid, "): ", data.get("message", ""))
		callback.call({})
		return
		
	var video_data = data.get("data", {})
	if video_data.is_empty():
		callback.call({})
		return

	var owner_d = video_data.get("owner", {})
	var stat = video_data.get("stat", {})
	var dimension = video_data.get("dimension", {})
	var subtitle_info = video_data.get("subtitle", {})
	var rights = video_data.get("rights", {})
	var pages: Array = video_data.get("pages", [])

	# 提取分P信息（只保留每个分P的必要数据，不包含其他视频）
	var pages_info = []
	for page in pages:
		pages_info.append({
			"cid": page.get("cid", 0),
			"page": page.get("page", 1),
			"part": page.get("part", ""),
			"duration": page.get("duration", 0),
			"dimension": page.get("dimension", {}),
			"first_frame": page.get("first_frame", ""),
			"vid": page.get("vid", ""),
			"weblink": page.get("weblink", "")
		})

	var info := {
		"link": video_data.get("bvid", bvid),
		"BV": video_data.get("bvid", bvid),
		"aid": video_data.get("aid", 0),
		"title": decode_html_entities(video_data.get("title", "")),
		"desc": decode_html_entities(video_data.get("desc", "")),
		"desc_v2": video_data.get("desc_v2", []),   # 结构化简介
		"author": decode_html_entities(owner_d.get("name", "")),
		"mid": owner_d.get("mid", 0),
		"face": owner_d.get("face", ""),            # UP 主头像
		"pic": video_data.get("pic", ""),
		"pubdate": video_data.get("pubdate", 0),
		"ctime": video_data.get("ctime", 0),
		"duration": video_data.get("duration", 0),
		"cid": video_data.get("cid", 0),
		"videos": video_data.get("videos", 1),
		"copyright": video_data.get("copyright", 1),
		"tid": video_data.get("tid", 0),
		"tname": video_data.get("tname", ""),
		"tid_v2": video_data.get("tid_v2", 0),
		"tname_v2": video_data.get("tname_v2", ""),
		"dynamic": video_data.get("dynamic", ""),
		"dimension": {
			"width": dimension.get("width", 0),
			"height": dimension.get("height", 0),
			"rotate": dimension.get("rotate", 0)
		},
		"rights": rights,                            # 完整的版权标志字典
		"stat": {                                    # 完整的统计数据
			"view": stat.get("view", 0),
			"danmaku": stat.get("danmaku", 0),
			"like": stat.get("like", 0),
			"coin": stat.get("coin", 0),
			"favorite": stat.get("favorite", 0),
			"share": stat.get("share", 0),
			"reply": stat.get("reply", 0),
			"now_rank": stat.get("now_rank", 0),
			"his_rank": stat.get("his_rank", 0),
			"dislike": stat.get("dislike", 0),
			"evaluation": stat.get("evaluation", "")
		},
		"subtitle": subtitle_info,                   # 包含 allow_submit 和 list
		"pages": pages_info,                         # 所有分P信息
		"season_id": video_data.get("season_id", 0)  # 合集 ID（只留数字，不取合集内容）
	}

	callback.call(info)
	
#region 需要用户登陆
const LYRICS_CACHE_DIR = "user://lyrics/"
const MAX_LYRICS_CACHE_SIZE = 500

var _lyrics_cache_index: Dictionary = {}
var _lyrics_cache_loaded: bool = false

var _pending_requests: Dictionary = {}
var _request_counter: int = 0
var _video_info_cache: Dictionary = {}

func _load_lyrics_cache_index() -> void:
	if _lyrics_cache_loaded:
		return
	var keys = GdScriptFunc.get_keys("LyricsCache")
	for key in keys:
		var entry = GdScriptFunc.get_data("LyricsCache", key)
		if typeof(entry) == TYPE_DICTIONARY:
			var file = entry.get("file", "")
			var time = entry.get("time", 0)
			if not file.is_empty():
				_lyrics_cache_index[key] = {"file": file, "time": time}
	_lyrics_cache_loaded = true

func _save_lyrics_cache_index() -> void:
	var old_keys = GdScriptFunc.get_keys("LyricsCache")
	for key in old_keys:
		GdScriptFunc.remove_key("LyricsCache", key)
	for key in _lyrics_cache_index:
		var entry: Dictionary = _lyrics_cache_index[key]
		GdScriptFunc.set_data("LyricsCache", key, entry)

func _get_lyrics_cache_key(request_id: String) -> String:
	return request_id.md5_text()

func _add_lyrics_to_cache(request_id: String, source_path: String) -> String:
	_load_lyrics_cache_index()
	var key = _get_lyrics_cache_key(request_id)
	var filename = key + ".lrc"
	var dest_path = LYRICS_CACHE_DIR + filename

	if not DirAccess.dir_exists_absolute(LYRICS_CACHE_DIR):
		DirAccess.make_dir_recursive_absolute(LYRICS_CACHE_DIR)

	var src_file = FileAccess.open(source_path, FileAccess.READ)
	if src_file:
		var data = src_file.get_buffer(src_file.get_length())
		src_file.close()
		var dst_file = FileAccess.open(dest_path, FileAccess.WRITE)
		if dst_file:
			dst_file.store_buffer(data)
			dst_file.close()
		else:
			push_error("无法写入歌词缓存: ", dest_path)
			return source_path
	else:
		push_error("无法读取源歌词文件: ", source_path)
		return source_path

	var now = Time.get_unix_time_from_system()
	_lyrics_cache_index[key] = {"file": filename, "time": now}

	if _lyrics_cache_index.size() > MAX_LYRICS_CACHE_SIZE:
		var sorted = []
		for k in _lyrics_cache_index:
			sorted.append({"key": k, "time": _lyrics_cache_index[k]["time"]})
		sorted.sort_custom(func(a, b): return a["time"] < b["time"])
		var to_remove = _lyrics_cache_index.size() - MAX_LYRICS_CACHE_SIZE
		for i in range(to_remove):
			var old_key: String = sorted[i]["key"]
			var old_file: String = _lyrics_cache_index[old_key]["file"]
			var old_path = LYRICS_CACHE_DIR + old_file
			if FileAccess.file_exists(old_path):
				DirAccess.remove_absolute(old_path)
			_lyrics_cache_index.erase(old_key)

	_save_lyrics_cache_index()
	return dest_path

func _get_cached_lyrics(request_id: String) -> String:
	_load_lyrics_cache_index()
	var key = _get_lyrics_cache_key(request_id)
	if not _lyrics_cache_index.has(key):
		return ""
	var entry: Dictionary = _lyrics_cache_index[key]
	var file_path = LYRICS_CACHE_DIR + entry["file"]
	if not FileAccess.file_exists(file_path):
		_lyrics_cache_index.erase(key)
		_save_lyrics_cache_index()
		return ""
	return file_path


# ==================== 请求 ID 与音频路径 ====================
func _make_request_id() -> String:
	_request_counter += 1
	return str(_request_counter) + "_" + str(Time.get_ticks_msec())

func _get_current_audio_file_path() -> String:
	if not M4SAudioPlayer:
		push_error("M4SAudioPlayer 自动加载未就绪")
		return ""
	return M4SAudioPlayer.CurrentAudioFilePath

func _get_cached_video_info(bvid: String) -> Dictionary:
	return _video_info_cache.get(bvid, {})


# ==================== 纯音乐检测 ====================
func _check_music_ratio(subtitle_content) -> bool:
	if typeof(subtitle_content) != TYPE_DICTIONARY:
		return false
	var body = subtitle_content.get("body", [])
	if not body is Array or body.is_empty():
		return false
	var music_count = 0
	for entry in body:
		if typeof(entry) != TYPE_DICTIONARY:
			continue
		var content: String = entry.get("content", "")
		if content.begins_with("♪") and content.ends_with("♪"):
			content = content.substr(1, content.length() - 2)
		content = content.strip_edges()
		if content == "音乐" or content.to_lower() == "music":
			music_count += 1
	var total = body.size()
	if total == 0:
		return false
	var ratio = float(music_count) / total
	return ratio > 0.4


# ==================== 字幕下载与处理 ====================
func _try_download_candidate(index: int, candidates: Array, bvid: String, callback: Callable, skip_correction: bool = false, save_path: String = "") -> void:
	if index >= candidates.size():
		push_error("所有候选字幕均不符合条件或被跳过 (", bvid, ")")
		callback.call({})
		return

	var candidate = candidates[index]
	var subtitle_url: String = candidate["url"]
	var is_ai: bool = candidate.get("is_ai", true)   # 默认视为 AI，保守处理

	if subtitle_url.begins_with("//"):
		subtitle_url = "https:" + subtitle_url

	var http = HTTPRequest.new()
	add_child(http)
	http.request_completed.connect(func(_result, resp_code, _resp_headers, resp_body):
		http.queue_free()
		if resp_code != 200:
			push_warning("下载字幕文件失败 (", bvid, "): HTTP ", resp_code, " 尝试下一个")
			_try_download_candidate(index + 1, candidates, bvid, callback, skip_correction, save_path)
			return

		var subtitle_data = JSON.new()
		if subtitle_data.parse(resp_body.get_string_from_utf8()) != OK:
			push_warning("字幕文件JSON解析失败 (", bvid, ") 尝试下一个")
			_try_download_candidate(index + 1, candidates, bvid, callback, skip_correction, save_path)
			return

		var subtitle_content = subtitle_data.get_data()
		if typeof(subtitle_content) != TYPE_DICTIONARY:
			push_warning("字幕JSON结构异常 (", bvid, ") 尝试下一个")
			_try_download_candidate(index + 1, candidates, bvid, callback, skip_correction, save_path)
			return

		if _check_music_ratio(subtitle_content):
			push_warning("字幕中纯音乐标记占比过高 (", bvid, "), 尝试下一个")
			_try_download_candidate(index + 1, candidates, bvid, callback, skip_correction, save_path)
			return

		# 决定是否真正跳过修正：如果是非 AI 字幕，强制跳过；如果是 AI 字幕，由外部 skip_correction 或选项控制
		var final_skip = false
		if not is_ai:
			final_skip = true
		else:
			final_skip = skip_correction or not GdScriptFunc.get_data("Options", "SubtitleTextCorrection", false)

		if final_skip:
			_generate_bilibili_lrc(subtitle_content, bvid, callback, save_path)
		else:
			_perform_subtitle_correction(subtitle_content, bvid, callback, save_path)
	)

	var download_headers = _get_image_headers()
	download_headers.append("Referer: https://www.bilibili.com/video/" + bvid)
	var error = http.request(subtitle_url, download_headers, HTTPClient.METHOD_GET)
	if error != OK:
		push_warning("字幕下载请求失败 (", bvid, "): ", error, " 尝试下一个")
		http.queue_free()
		_try_download_candidate(index + 1, candidates, bvid, callback, skip_correction, save_path)

func _generate_bilibili_lrc(subtitle_content: Dictionary, _bvid: String, callback: Callable, save_path: String = "") -> void:
	var body = subtitle_content.get("body", [])
	if body.is_empty():
		callback.call({})
		return

	var lrc_path: String
	if not save_path.is_empty():
		lrc_path = save_path
		DirAccess.make_dir_recursive_absolute(lrc_path.get_base_dir())
	else:
		var audio_path = _get_current_audio_file_path()
		if audio_path.is_empty():
			push_error("无法自动生成字幕文件名：当前无播放中的音频，且未提供 save_path")
			callback.call({})
			return
		var base_name = audio_path.get_file().get_basename()
		lrc_path = ProjectSettings.globalize_path(LYRICS_CACHE_DIR).path_join(base_name + ".lrc")
		DirAccess.make_dir_recursive_absolute(lrc_path.get_base_dir())

	var lrc_text = ""
	for entry in body:
		var from_sec: float = entry.get("from", 0.0)
		var content: String = entry.get("content", "")
		content = content.replace("♪", "").strip_edges()
		if content.is_empty():
			continue
		var minutes = int(from_sec / 60)
		var seconds = int(from_sec) % 60
		var milliseconds = int(round((from_sec - int(from_sec)) * 100))
		lrc_text += "[%02d:%02d.%02d]%s\n" % [minutes, seconds, milliseconds, content]

	var file = FileAccess.open(lrc_path, FileAccess.WRITE)
	if file:
		file.store_string(lrc_text)
		file.close()
		callback.call({"type": "aligned_lrc", "path": lrc_path})
	else:
		push_error("无法写入 LRC 文件: ", lrc_path)
		callback.call({})


# ==================== 字幕修正入口 (调用 C#) ====================
func _perform_subtitle_correction(subtitle_content: Dictionary, bvid: String, callback: Callable, save_path: String = "") -> void:
	if not has_node("/root/SubtitleCorrection"):
		push_error("SubtitleCorrection 未找到，回退到 B 站字幕")
		_generate_bilibili_lrc(subtitle_content, bvid, callback, save_path)
		return

	var audio_path = _get_current_audio_file_path()
	if audio_path.is_empty():
		callback.call(subtitle_content)
		return

	var info = _get_cached_video_info(bvid)
	var track_name = CSharpFunc.ExtractSongName(info.get("title", ""))
	var output_dir = LYRICS_CACHE_DIR
	var request_id = _make_request_id()

	_pending_requests[request_id] = {
		"callback": callback,
		"fallback": subtitle_content,
		"save_path": save_path
	}

	# 调用 C# 方法（自动加载可直接通过类名调用）
	SubtitleCorrection.ProcessSubtitleAsync(
		subtitle_content, audio_path, track_name, output_dir, request_id
	)


func _fallback_external_lyrics(info: Dictionary, callback: Callable, save_path: String = "") -> void:
	if not has_node("/root/SubtitleCorrection"):
		push_error("SubtitleCorrection 未找到，无法获取外部歌词")
		callback.call({})
		return

	var audio_path = _get_current_audio_file_path()
	if audio_path.is_empty():
		callback.call({})
		return

	var track_name = CSharpFunc.ExtractSongName(info.get("title", ""))
	var output_dir = LYRICS_CACHE_DIR
	var request_id = _make_request_id()

	_pending_requests[request_id] = {
		"callback": callback,
		"fallback": {},
		"save_path": save_path
	}

	SubtitleCorrection.FetchAndAlignExternalAsync(
		audio_path, track_name, output_dir, request_id
	)


# ==================== 信号处理（C# 处理完毕的回调） ====================
func _on_subtitle_processed(lrc_path: String, request_id: String):
	if not _pending_requests.has(request_id):
		return
	var ctx = _pending_requests[request_id]
	_pending_requests.erase(request_id)
	var callback: Callable = ctx["callback"]
	var fallback = ctx.get("fallback", null)
	var save_path: String = ctx.get("save_path", "")

	# 优先使用缓存
	var cached = ""
	if save_path.is_empty():
		cached = _get_cached_lyrics(request_id)
		if cached and not cached.is_empty():
			callback.call({"type": "aligned_lrc", "path": cached})
			return

	var source_path = ""
	if lrc_path and not lrc_path.is_empty():
		source_path = lrc_path
	else:
		# 修正失败，有 B 站字幕回退则生成 LRC
		if typeof(fallback) == TYPE_DICTIONARY and fallback.has("body"):
			_generate_bilibili_lrc(fallback, "", callback, save_path)
			return
		else:
			callback.call(fallback)
			return

	# 如果指定了 save_path，直接复制
	if not save_path.is_empty():
		DirAccess.make_dir_recursive_absolute(save_path.get_base_dir())
		var src_file = FileAccess.open(source_path, FileAccess.READ)
		if src_file:
			var data = src_file.get_buffer(src_file.get_length())
			src_file.close()
			var dst_file = FileAccess.open(save_path, FileAccess.WRITE)
			if dst_file:
				dst_file.store_buffer(data)
				dst_file.close()
				callback.call({"type": "aligned_lrc", "path": save_path})
			else:
				push_error("无法写入指定路径: ", save_path)
				callback.call({"type": "aligned_lrc", "path": source_path})
		else:
			push_error("无法读取修正后的文件: ", source_path)
			callback.call({})
	else:
		var final_path = _add_lyrics_to_cache(request_id, source_path)
		callback.call({"type": "aligned_lrc", "path": final_path})


# ==================== 公共 API ====================
func fetch_subtitle_auto(bvid: String, callback: Callable, save_path: String = "") -> void:
	fetch_video_info(bvid, func(info: Dictionary):
		if info.is_empty():
			callback.call(null)
			return
		_video_info_cache[bvid] = info
		var cid = info.get("cid", 0)
		if cid == 0:
			push_error("未能获取到有效 cid")
			callback.call(null)
			return

		var subtitle_callback = func(subtitle_data):
			if subtitle_data == null or (typeof(subtitle_data) == TYPE_DICTIONARY and subtitle_data.is_empty()):
				_fallback_external_lyrics(info, callback, save_path)
			else:
				callback.call(subtitle_data)

		_fetch_subtitle_with_cid(bvid, cid, subtitle_callback, save_path)
	)

func fetch_subtitle_with_info(info: Dictionary, callback: Callable, save_path: String = "") -> void:
	if info.is_empty():
		callback.call({})
		return
	var bvid = info.get("link", "")
	var cid = info.get("cid", 0)
	if bvid.is_empty() or cid == 0:
		push_error("提供的 info 中缺少 bvid 或 cid")
		callback.call({})
		return

	_video_info_cache[bvid] = info

	var subtitle_callback = func(subtitle_data):
		if subtitle_data == null or (typeof(subtitle_data) == TYPE_DICTIONARY and subtitle_data.is_empty()):
			_fallback_external_lyrics(info, callback, save_path)
		else:
			callback.call(subtitle_data)

	_fetch_subtitle_with_cid(bvid, cid, subtitle_callback, save_path)


# ==================== 内部：B站 API 请求与字幕候选 ====================
func _fetch_subtitle_with_cid(bvid: String, cid: int, callback: Callable, save_path: String = "") -> void:
	var url = "https://api.bilibili.com/x/player/wbi/v2?bvid=%s&cid=%d" % [bvid, cid]
	url = await _sign_wbi_url(url)
	_request(url, _on_subtitle_player_info_received, [bvid, cid, callback, save_path])

func _on_subtitle_player_info_received(_result: int, response_code: int, _headers: PackedStringArray, body: PackedByteArray, extra: Variant) -> void:
	var extra_arr: Array = extra
	var bvid: String = extra_arr[0]
	var callback: Callable = extra_arr[2]
	var save_path: String = extra_arr[3] if extra_arr.size() > 3 else ""

	if response_code != 200:
		push_error("获取字幕列表失败 (", bvid, "): HTTP ", response_code)
		callback.call({})
		return

	var json = JSON.new()
	if json.parse(body.get_string_from_utf8()) != OK:
		push_error("字幕列表JSON解析失败 (", bvid, ")")
		callback.call({})
		return

	var root = json.get_data()
	if root.get("code") != 0:
		push_error("字幕列表API返回错误 (", bvid, "): ", root.get("message"))
		callback.call({})
		return

	var data = root.get("data", {})
	var subtitle_info = data.get("subtitle", {})
	# ★ 关键修正：B站API返回的字幕列表字段是 "subtitles"，不是 "list"
	var subtitles: Array = subtitle_info.get("subtitles", [])

	if subtitles.is_empty():
		# 有时极旧视频可能还藏在 ai_subtitle 里，兜底尝试
		var ai_sub = subtitle_info.get("ai_subtitle", {})
		subtitles = ai_sub.get("subtitles", [])

	var video_lang = subtitle_info.get("video_lang", "")
	if video_lang.is_empty():
		video_lang = "zh-CN"

	var non_ai_candidates = []
	var ai_candidates = []

	for sub in subtitles:
		var lan = sub.get("lan", "")
		# 优先使用 subtitle_url，若为空则尝试 subtitle_url_v2（部分视频可能只有 v2）
		var url = sub.get("subtitle_url", "")
		if url.is_empty():
			url = sub.get("subtitle_url_v2", "")
		if url.is_empty():
			continue

		if url.begins_with("//"):
			url = "https:" + url

		if not lan.begins_with("ai-"):
			non_ai_candidates.append({"url": url, "is_ai": false, "lan": lan})
		else:
			ai_candidates.append({"url": url, "is_ai": true, "lan": lan})

	# 1) 优先非 AI 字幕
	if not non_ai_candidates.is_empty():
		non_ai_candidates.sort_custom(func(a, b):
			var a_prio = 0
			var b_prio = 0
			if a["lan"] == video_lang: a_prio = 2
			elif a["lan"] == "zh-CN": a_prio = 1
			if b["lan"] == video_lang: b_prio = 2
			elif b["lan"] == "zh-CN": b_prio = 1
			return a_prio > b_prio
		)
		_try_download_candidate(0, non_ai_candidates, bvid, callback, false, save_path)
		return

	# 2) 其次 AI 字幕
	if not ai_candidates.is_empty():
		var video_lang_prefix = video_lang.split("-")[0]
		var preferred_ai_lang = "ai-" + video_lang_prefix
		ai_candidates.sort_custom(func(a, b):
			var a_prio = 0; var b_prio = 0
			if a["lan"] == preferred_ai_lang: a_prio = 3
			elif a["lan"] == "ai-zh": a_prio = 2
			elif a["lan"] == "ai-en": a_prio = 1
			if b["lan"] == preferred_ai_lang: b_prio = 3
			elif b["lan"] == "ai-zh": b_prio = 2
			elif b["lan"] == "ai-en": b_prio = 1
			return a_prio > b_prio
		)
		_try_download_candidate(0, ai_candidates, bvid, callback, false, save_path)
		return

	# 3) 全都没有可用字幕 URL → 走外部歌词兜底
	push_warning("该视频无可用字幕 (", bvid, ")，尝试纯外部歌词")
	# callback.call({}) 会被外部调用者检测到，并触发 _fallback_external_lyrics
	callback.call({})
func _pick_best_subtitle_url(sub_list: Array, prefer_lang: String) -> String:
	for sub in sub_list:
		if sub.get("lan", "") == prefer_lang:
			return sub.get("subtitle_url", "")
	if not sub_list.is_empty():
		return sub_list[0].get("subtitle_url", "")
	return ""
const MIXIN_KEY_ENC_TAB = [
	46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35,
	27, 43, 5, 49, 33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13,
	37, 48, 7, 16, 24, 55, 40, 61, 26, 17, 0, 1, 60, 51, 30, 4,
	22, 25, 54, 21, 56, 59, 6, 63, 57, 62, 11, 36, 20, 52, 34, 44
]
var _wbi_key_cache = { "img_key": "", "sub_key": "", "cached_time": 0 }

func _get_wbi_key() -> Dictionary:
	var now: int = Time.get_unix_time_from_system()
	if now - _wbi_key_cache.get("cached_time", 0) < 1800 and not _wbi_key_cache.get("img_key", "").is_empty():
		return _wbi_key_cache

	var http := HTTPRequest.new()
	add_child(http)
	var headers: PackedStringArray = _get_headers()
	var error := http.request("https://api.bilibili.com/x/web-interface/nav", headers, HTTPClient.METHOD_GET)
	if error != OK:
		push_error("获取 WBI 密钥请求失败: ", error)
		http.queue_free()
		return _wbi_key_cache

	var result: Array = await http.request_completed
	http.queue_free()
	print("请求结果码:", result[0])
	print("系统时间:", Time.get_unix_time_from_system())
	var response_code: int = result[1]
	var body_str: String = (result[3] as PackedByteArray).get_string_from_utf8()

	if response_code != 200:
		push_error("WBI 密钥接口 HTTP ", response_code)
		
		return _wbi_key_cache

	var json := JSON.new()
	if json.parse(body_str) != OK:
		push_error("WBI 密钥 JSON 解析失败")
		return _wbi_key_cache

	var root: Dictionary = json.get_data()
	var data: Dictionary = root.get("data", {})
	var wbi_img: Dictionary = data.get("wbi_img", {})

	var img_key: String = GdScriptFunc.extract_key_from_url(wbi_img.get("img_url", ""))
	var sub_key: String = GdScriptFunc.extract_key_from_url(wbi_img.get("sub_url", ""))

	if img_key.is_empty() or sub_key.is_empty():
		push_error("WBI 密钥提取失败")
		return _wbi_key_cache

	_wbi_key_cache["img_key"] = img_key
	_wbi_key_cache["sub_key"] = sub_key
	_wbi_key_cache["cached_time"] = now
	return _wbi_key_cache

func _sign_wbi_url(url: String) -> String:
	var key_data: Dictionary = await _get_wbi_key()
	var img_key: String = key_data.get("img_key", "")
	var sub_key: String = key_data.get("sub_key", "")

	if img_key.is_empty() or sub_key.is_empty():
		push_error("WBI 密钥不完整，无法签名 URL")
		return url

	var combined_key: String = img_key + sub_key
	if combined_key.length() < 64:
		push_error("WBI 密钥总长度不足 64，实际长度：%d" % combined_key.length())
		return url

	var wbi_key: String = ""
	for idx: int in MIXIN_KEY_ENC_TAB:
		wbi_key += combined_key[idx]

	# 解析 URL
	var uri: String = url.replace("https://api.bilibili.com", "")
	var query_split: PackedStringArray = uri.split("?", false, 1)
	var base: String = query_split[0]
	var query_string: String = query_split[1] if query_split.size() > 1 else ""

	var params: Dictionary = {}
	for param in query_string.split("&"):
		var kv = param.split("=")
		if kv.size() == 2:
			params[kv[0]] = kv[1].uri_decode()  # 解码

	# 移除已有的 w_rid 和 wts（如果有），避免重复
	params.erase("w_rid")
	params.erase("wts")

	# 添加新的 wts（整数）
	var wts: int = Time.get_unix_time_from_system()
	params["wts"] = wts

	# 按键名排序
	var sorted_keys = params.keys()
	sorted_keys.sort()

	# 拼接用于签名的字符串（原始值）
	var sorted_query = ""
	for key in sorted_keys:
		if not sorted_query.is_empty():
			sorted_query += "&"
		sorted_query += key + "=" + str(params[key])  # 原始值

	var sign_str = sorted_query + wbi_key
	var w_rid = sign_str.md5_text()

	# 构建最终 URL，将所有参数编码并添加 w_rid
	var final_parts = []
	for key in sorted_keys:
		var value = str(params[key]).uri_encode()
		final_parts.append(key + "=" + value)
	# 添加 w_rid（不编码，它是十六进制）
	final_parts.append("w_rid=" + w_rid)

	var final_url = "https://api.bilibili.com" + base + "?" + "&".join(final_parts)
	return final_url

func start_qr_login(login_callback: Callable) -> void:
	on_qr_login_result = login_callback
	var http = HTTPRequest.new()
	add_child(http)
	http.request_completed.connect(_on_qr_generated)
	var err = http.request("https://passport.bilibili.com/x/passport-login/web/qrcode/generate", PackedStringArray(), HTTPClient.METHOD_GET)

var on_qr_login_result: Callable

func _on_qr_generated(_result: int, response_code: int, _headers: PackedStringArray, body: PackedByteArray) -> void:
	if response_code != 200: return
	var json = JSON.new()
	if json.parse(body.get_string_from_utf8()) != OK: return
	var data = json.get_data()["data"]
	var url = data["url"]
	var qrcode_key = data["qrcode_key"]
	_display_qrcode(url)
	_poll_login_status(qrcode_key)

var qr_window: Window = null

func _display_qrcode(content: String) -> void:
	qr_window=preload("res://Scene/Log_in.tscn").instantiate()
	qr_window.close_requested.connect(_on_qr_window_closed)
	add_child(qr_window)
	GdScriptFunc.apply_theme_and_styles_to_node(qr_window)
	var encoded = content.uri_encode()
	var qr_api = "https://api.qrserver.com/v1/create-qr-code/?size=200x200&data=" + encoded
	var img_request = HTTPRequest.new()
	add_child(img_request)
	img_request.request_completed.connect(func(_r, _c, _h, body):
		if not is_instance_valid(qr_window):
			return
		var img = Image.new()
		if img.load_png_from_buffer(body) == OK:
			var tex = ImageTexture.create_from_image(img)
			qr_window.get_node("QRImage").texture = tex
		else:
			push_error("二维码图片加载失败")
	)
	img_request.request(qr_api, PackedStringArray(), HTTPClient.METHOD_GET)

func _on_qr_window_closed() -> void:
	if qr_window:
		qr_window.queue_free()
		qr_window = null
	if _poll_timer:
		_poll_timer.stop()
		_poll_timer.queue_free()
		_poll_timer = null
	if _close_delay_timer:
		_close_delay_timer.stop()
		_close_delay_timer.queue_free()
		_close_delay_timer = null
	if on_qr_login_result:
		on_qr_login_result.call(false)

func _close_qr_window() -> void:
	if qr_window:
		qr_window.queue_free()
		qr_window = null
	if _poll_timer:
		_poll_timer.stop()
		_poll_timer.queue_free()
		_poll_timer = null
	if _close_delay_timer:
		_close_delay_timer.stop()
		_close_delay_timer.queue_free()
		_close_delay_timer = null
var _poll_timer: Timer

func _poll_login_status(qrcode_key: String) -> void:
	_poll_timer = Timer.new()
	_poll_timer.wait_time = 2.0
	_poll_timer.autostart = true
	_poll_timer.timeout.connect(_check_qr_status.bind(qrcode_key))
	add_child(_poll_timer)

func _check_qr_status(qrcode_key: String) -> void:
	var http = HTTPRequest.new()
	add_child(http)
	http.request_completed.connect(func(result, response_code, headers, body):
		if response_code != 200: return
		var json = JSON.new()
		if json.parse(body.get_string_from_utf8()) != OK: return
		var data = json.get_data()["data"]
		var code = data["code"]
		if code == 0:
			# 停止轮询
			if _poll_timer:
				_poll_timer.stop()
			# 换取 cookie，后续的头像加载和延迟关闭将在 _exchange_cookie 内部处理
			_exchange_cookie(data["url"])
		elif code == 86038:
			if on_qr_login_result:
				on_qr_login_result.call(false)
			_close_qr_window()
	)
	http.request("https://passport.bilibili.com/x/passport-login/web/qrcode/poll?qrcode_key=" + qrcode_key, PackedStringArray(), HTTPClient.METHOD_GET)
func _exchange_cookie(login_url: String) -> void:
	var http = HTTPRequest.new()
	add_child(http)
	http.max_redirects = 0

	var buvid3 = get_or_generate_buvid()
	var cookie_str = "buvid3=" + buvid3 + "; b_nut=" + str(Time.get_unix_time_from_system())
	var headers = PackedStringArray([
		"User-Agent: Mozilla/5.0 ...",
		"Referer: https://www.bilibili.com",
		"Cookie: " + cookie_str
	])

	http.request_completed.connect(func(result, response_code, resp_headers, body):
		# 提取并保存所有登录 Cookie
		for header in resp_headers:
			if header.begins_with("Set-Cookie: "):
				var cookie_part = header.trim_prefix("Set-Cookie: ")
				var parts = cookie_part.split(";")
				if parts.size() > 0:
					var kv = parts[0].strip_edges()
					var eq_pos = kv.find("=")
					if eq_pos != -1:
						var key = kv.substr(0, eq_pos)
						var value = kv.substr(eq_pos + 1)
						match key:
							"SESSDATA":
								GdScriptFunc.set_data("AccountData", "SESSDATA", value)
							"bili_jct":
								GdScriptFunc.set_data("AccountData", "bili_jct", value)
							"DedeUserID":
								GdScriptFunc.set_data("AccountData", "DedeUserID", value)
							"DedeUserID__ckMd5":
								GdScriptFunc.set_data("AccountData", "DedeUserID__ckMd5", value)
							"sid":
								GdScriptFunc.set_data("AccountData", "sid", value)
							"bp_t_offset":   # 注意：key 可能是 "bp_t_offset_1909594131" 这种动态形式，需要正则匹配
								var offset_value = value
								var uid = GdScriptFunc.get_data("AccountData", "DedeUserID", "")
								if uid != "":
									GdScriptFunc.set_data("AccountData", "bp_t_offset", offset_value)
							"bili_ticket":
								GdScriptFunc.set_data("AccountData", "bili_ticket", value)
							"bili_ticket_expires":
								GdScriptFunc.set_data("AccountData", "bili_ticket_expires", value)
		print("所有登录 cookie 已保存")
		# Cookie 保存完毕，开始加载头像并延迟关闭
		_load_avatar_and_delayed_close()
	)
	var err = http.request(login_url, headers, HTTPClient.METHOD_GET)
	if err != OK:
		push_error("请求失败: ", err)
		# 请求失败也直接关闭窗口，避免卡住
		_close_qr_window()
		if on_qr_login_result:
			on_qr_login_result.call(false)
var _close_delay_timer: Timer = null

func _load_avatar_and_delayed_close() -> void:
	fetch_user_avatar(func(texture: ImageTexture):
		if is_instance_valid(qr_window) and texture != null:
			qr_window.get_node("QRImage").texture = texture
		_start_delayed_close()
	)

func _start_delayed_close() -> void:
	# 确保窗口还存在
	if not is_instance_valid(qr_window):
		return
	# 创建 0.5 秒延迟定时器
	_close_delay_timer = Timer.new()
	_close_delay_timer.wait_time = 0.5
	_close_delay_timer.one_shot = true
	_close_delay_timer.timeout.connect(_on_delayed_close_timeout)
	add_child(_close_delay_timer)
	_close_delay_timer.start()

func _on_delayed_close_timeout() -> void:
	if on_qr_login_result:
		on_qr_login_result.call(true)
	_close_qr_window()
func fetch_user_avatar(callback: Callable) -> void:
	# 1. 检查必要 Cookie
	var sessdata = GdScriptFunc.get_data("AccountData", "SESSDATA")
	if sessdata == null or sessdata == "":
		callback.call(null)
		return

	# 2. 构造带 Cookie 的请求头
	var cookie_str = "SESSDATA=" + sessdata
	var bili_jct = GdScriptFunc.get_data("AccountData", "bili_jct")
	if bili_jct != null:
		cookie_str += "; bili_jct=" + bili_jct
	var dedeuserid = GdScriptFunc.get_data("AccountData", "DedeUserID")
	if dedeuserid != null:
		cookie_str += "; DedeUserID=" + dedeuserid

	var nav_headers = PackedStringArray([
		"User-Agent: Mozilla/5.0 ...",
		"Referer: https://www.bilibili.com",
		"Cookie: " + cookie_str
	])

	# 3. 请求导航信息，获取头像 URL
	var http_nav = HTTPRequest.new()
	add_child(http_nav)
	http_nav.request_completed.connect(func(result, response_code, headers, body):
		http_nav.queue_free()  # 请求完成后释放

		if response_code != 200:
			callback.call(null)
			return

		var json = JSON.new()
		if json.parse(body.get_string_from_utf8()) != OK:
			callback.call(null)
			return

		var data = json.get_data()
		var face_url = data.get("data", {}).get("face", "")
		if face_url == "":
			callback.call(null)
			return

		# 4. 下载头像图片
		var img_request = HTTPRequest.new()
		add_child(img_request)
		img_request.request_completed.connect(func(_r, _c, _h, img_body):
			img_request.queue_free()

			var img = Image.new()
			if img.load_jpg_from_buffer(img_body) == OK or img.load_png_from_buffer(img_body) == OK:
				var tex = ImageTexture.create_from_image(img)
				callback.call(tex)
			else:
				callback.call(null)
		)
		img_request.request(face_url, PackedStringArray(), HTTPClient.METHOD_GET)
	)

	http_nav.request("https://api.bilibili.com/x/web-interface/nav", nav_headers, HTTPClient.METHOD_GET)
#endregion
