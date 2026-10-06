class_name BilibiliCookieStore
extends RefCounted

static var _cached_buvid: String = ""

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

static func get_or_generate_buvid() -> String:
	if not _cached_buvid.is_empty():
		return _cached_buvid
	_cached_buvid = GdScriptFunc.get_data("Network", "buvid3", "")
	if not _cached_buvid.is_empty():
		return _cached_buvid
	_cached_buvid = generate_fingerprint_buvid()
	GdScriptFunc.set_data("Network", "buvid3", _cached_buvid)
	return _cached_buvid

static func generate_fingerprint_buvid() -> String:
	var sz = DisplayServer.screen_get_size()
	var info = [
		OS.get_name(),
		str(OS.get_processor_count()),
		str(sz.x), str(sz.y),
		OS.get_locale(),
		"GodotEngine/" + Engine.get_version_info().string,
		DisplayServer.get_name()
	]
	var h = "||".join(info).md5_text().to_upper()
	return h.substr(0, 8) + "-" + h.substr(8, 4) + "-" + h.substr(12, 4) + "-" + h.substr(16, 4) + "-" + h.substr(20, 12) + "infoc"

static func generate_fake_b_nut() -> String:
	return str(Time.get_unix_time_from_system())

static func random_string(length: int = 16) -> String:
	const CHARS = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"
	var res = ""
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
	var uuid = "%04x%04x-%04x-%04x-%04x-%04x%04x%04x" % [
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
