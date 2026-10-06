class_name BilibiliCookieStore
extends RefCounted

static var _cached_buvid: String = ""
static var _cached_buvid4: String = ""

# ==================== 动态 User-Agent ====================
static func get_dynamic_user_agent() -> String:
	match OS.get_name():
		"macOS":
			return "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.6 Safari/605.1.15"
		"Windows":
			return "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
		"Linux":
			return "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
		_:
			return "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"

# ==================== buvid 读写 ====================

## 同步获取 buvid3：
## 1) 内存缓存
## 2) config 持久化
## 3) 本地兜底指纹（尽力可用，但可能被风控）
static func get_or_generate_buvid() -> String:
	if not _cached_buvid.is_empty():
		return _cached_buvid
	_cached_buvid = GdScriptFunc.get_data("Network", "buvid3", "")
	if not _cached_buvid.is_empty():
		return _cached_buvid
	_cached_buvid = generate_fingerprint_buvid()
	GdScriptFunc.set_data("Network", "buvid3", _cached_buvid)
	return _cached_buvid

## 由 BilibiliAPI 在成功拉取官方指纹后调用，覆盖本地兜底值
static func set_official_buvid(b3: String, b4: String) -> void:
	if b3.is_empty():
		return
	_cached_buvid = b3
	if not b4.is_empty():
		_cached_buvid4 = b4
	GdScriptFunc.set_data("Network", "buvid3", b3, true)
	if not b4.is_empty():
		GdScriptFunc.set_data("Network", "buvid4", b4, true)

## 需要重新拉取时调用
static func clear_cached_buvid() -> void:
	_cached_buvid = ""
	_cached_buvid4 = ""

static func get_cached_buvid4() -> String:
	if not _cached_buvid4.is_empty():
		return _cached_buvid4
	_cached_buvid4 = GdScriptFunc.get_data("Network", "buvid4", "")
	return _cached_buvid4

# ==================== 指纹 / 随机串（保留原名，别改） ====================

## 注意：这个函数名不能改，BilibiliAPI.gd 里有静态转发引用它
static func generate_fingerprint_buvid() -> String:
	var sz := DisplayServer.screen_get_size()
	var info := [
		OS.get_name(),
		str(OS.get_processor_count()),
		str(sz.x),
		str(sz.y),
		OS.get_locale(),
		"GodotEngine/" + Engine.get_version_info().string,
		DisplayServer.get_name()
	]
	var h := "||".join(info).md5_text().to_upper()
	return h.substr(0, 8) + "-" + h.substr(8, 4) + "-" + h.substr(12, 4) + "-" + h.substr(16, 4) + "-" + h.substr(20, 12) + "infoc"

static func generate_fake_b_nut() -> String:
	return str(Time.get_unix_time_from_system())

static func random_string(length: int = 16) -> String:
	const CHARS = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"
	var res := ""
	for i in length:
		res += CHARS[randi() % CHARS.length()]
	return res

static func get_or_generate_cookie_field(key: String, generator: Callable) -> String:
	var val = GdScriptFunc.get_data("Network", key, "")
	if val.is_empty():
		val = generator.call()
		GdScriptFunc.set_data("Network", key, val)
	return val

static func generate_buvid4() -> String:
	var uuid := "%04x%04x-%04x-%04x-%04x-%04x%04x%04x" % [
		randi() % 0xFFFF, randi() % 0xFFFF, randi() % 0xFFFF,
		(randi() % 0xFFFF) | 0x4000, (randi() % 0xFFFF) | 0x8000,
		randi() % 0xFFFF, randi() % 0xFFFF, randi() % 0xFFFF
	]
	return uuid + "-" + str(Time.get_unix_time_from_system()) + "-" + random_string(20)

static func generate_fingerprint() -> String:
	return random_string(32).md5_text()

static func generate_rpdid() -> String:
	return random_string(30)

static func generate_b_lsid() -> String:
	return random_string(8).to_upper() + "_" + random_string(12).to_upper()

static func generate_uuid() -> String:
	return random_string(8).to_upper() + "-" + random_string(4) + "-" + random_string(4) + "-" + random_string(4) + "-" + random_string(12).to_upper() + "infoc"
