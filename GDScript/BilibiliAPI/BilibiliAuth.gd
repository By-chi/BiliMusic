class_name BilibiliAuth
extends RefCounted

var _http: BilibiliHttpClient

var qr_window: Window = null
var on_qr_login_result: Callable
var _poll_timer: Timer
var _close_delay_timer: Timer

func _init(http: BilibiliHttpClient) -> void:
	_http = http

# ---------------- 二维码登录 ----------------

func start_login(login_callback: Callable) -> void:
	on_qr_login_result = login_callback
	var http = HTTPRequest.new()
	_http.host.add_child(http)
	http.request_completed.connect(_on_qr_generated)
	var err = http.request(
		"https://passport.bilibili.com/x/passport-login/web/qrcode/generate",
		PackedStringArray(),
		HTTPClient.METHOD_GET
	)
	if err != OK:
		push_error("[BilibiliAPI] 二维码生成请求失败: %d" % err)
		http.queue_free()

func _on_qr_generated(_result: int, response_code: int, _headers: PackedStringArray, body: PackedByteArray) -> void:
	if response_code != 200:
		return
	var json = JSON.new()
	if json.parse(body.get_string_from_utf8()) != OK:
		return
	var data = json.get_data()["data"]
	var url = data["url"]
	var qrcode_key = data["qrcode_key"]
	_display_qrcode(url)
	_poll_login_status(qrcode_key)

func _display_qrcode(content: String) -> void:
	qr_window = preload("res://Scene/Log_in.tscn").instantiate()
	qr_window.close_requested.connect(_on_qr_window_closed)
	_http.host.add_child(qr_window)
	GdScriptFunc.apply_theme_and_styles_to_node(qr_window)
	var encoded = content.uri_encode()
	var qr_api = "https://api.qrserver.com/v1/create-qr-code/?size=200x200&data=" + encoded
	var img_request = HTTPRequest.new()
	_http.host.add_child(img_request)
	img_request.request_completed.connect(func(_r, _c, _h, body):
		if not is_instance_valid(qr_window):
			return
		var img = Image.new()
		if img.load_png_from_buffer(body) == OK:
			var tex = ImageTexture.create_from_image(img)
			qr_window.get_node("QRImage").texture = tex
		else:
			push_error("[BilibiliAPI] 二维码图片加载失败")
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
	_on_qr_window_closed()

func _poll_login_status(qrcode_key: String) -> void:
	_poll_timer = Timer.new()
	_poll_timer.wait_time = 2.0
	_poll_timer.autostart = true
	_poll_timer.timeout.connect(_check_qr_status.bind(qrcode_key))
	_http.host.add_child(_poll_timer)

func _check_qr_status(qrcode_key: String) -> void:
	var http = HTTPRequest.new()
	_http.host.add_child(http)
	http.request_completed.connect(func(_result, response_code, _headers, body):
		if response_code != 200:
			return
		var json = JSON.new()
		if json.parse(body.get_string_from_utf8()) != OK:
			return
		var data = json.get_data()["data"]
		var code = data["code"]
		if code == 0:
			if _poll_timer:
				_poll_timer.stop()
			_exchange_cookie(data["url"])
		elif code == 86038:
			if on_qr_login_result:
				on_qr_login_result.call(false)
			_close_qr_window()
	)
	http.request(
		"https://passport.bilibili.com/x/passport-login/web/qrcode/poll?qrcode_key=" + qrcode_key,
		PackedStringArray(),
		HTTPClient.METHOD_GET
	)

func _exchange_cookie(login_url: String) -> void:
	var http = HTTPRequest.new()
	_http.host.add_child(http)
	http.max_redirects = 0

	var buvid3 = BilibiliCookieStore.get_or_generate_buvid()
	var cookie_str = "buvid3=" + buvid3 + "; b_nut=" + str(Time.get_unix_time_from_system())
	var headers = PackedStringArray([
		"User-Agent: " + BilibiliCookieStore.get_dynamic_user_agent(),
		"Referer: https://www.bilibili.com",
		"Cookie: " + cookie_str
	])

	http.request_completed.connect(func(_result, _response_code, resp_headers, _body):
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
							"bili_ticket":
								GdScriptFunc.set_data("AccountData", "bili_ticket", value)
							"bili_ticket_expires":
								GdScriptFunc.set_data("AccountData", "bili_ticket_expires", value)
		_load_avatar_and_delayed_close()
	)
	var err = http.request(login_url, headers, HTTPClient.METHOD_GET)
	if err != OK:
		push_error("[BilibiliAPI] 请求失败: %d" % err)
		_close_qr_window()
		if on_qr_login_result:
			on_qr_login_result.call(false)

func _load_avatar_and_delayed_close() -> void:
	fetch_avatar(func(texture: ImageTexture):
		if is_instance_valid(qr_window) and texture != null:
			qr_window.get_node("QRImage").texture = texture
		_start_delayed_close()
	)

func _start_delayed_close() -> void:
	if not is_instance_valid(qr_window):
		return
	_close_delay_timer = Timer.new()
	_close_delay_timer.wait_time = 0.5
	_close_delay_timer.one_shot = true
	_close_delay_timer.timeout.connect(_on_delayed_close_timeout)
	_http.host.add_child(_close_delay_timer)
	_close_delay_timer.start()

func _on_delayed_close_timeout() -> void:
	if on_qr_login_result:
		on_qr_login_result.call(true)
	_close_qr_window()

# ---------------- 用户头像 & CSRF ----------------

func fetch_avatar(callback: Callable) -> void:
	var sessdata = GdScriptFunc.get_data("AccountData", "SESSDATA")
	if sessdata == null or sessdata == "":
		callback.call(null)
		return

	var cookie_str = "SESSDATA=" + sessdata
	var bili_jct = GdScriptFunc.get_data("AccountData", "bili_jct")
	if bili_jct != null:
		cookie_str += "; bili_jct=" + bili_jct
	var dedeuserid = GdScriptFunc.get_data("AccountData", "DedeUserID")
	if dedeuserid != null:
		cookie_str += "; DedeUserID=" + dedeuserid

	var nav_headers = PackedStringArray([
		"User-Agent: " + BilibiliCookieStore.get_dynamic_user_agent(),
		"Referer: https://www.bilibili.com",
		"Cookie: " + cookie_str
	])

	var http_nav = HTTPRequest.new()
	_http.host.add_child(http_nav)
	http_nav.request_completed.connect(func(_result, response_code, _headers, body):
		http_nav.queue_free()
		if response_code != 200:
			callback.call(null); return
		var json = JSON.new()
		if json.parse(body.get_string_from_utf8()) != OK:
			callback.call(null); return
		var face_url = json.get_data().get("data", {}).get("face", "")
		if face_url == "":
			callback.call(null); return
		var img_request = HTTPRequest.new()
		_http.host.add_child(img_request)
		img_request.request_completed.connect(func(_r, _c, _h, img_body):
			img_request.queue_free()
			var img = Image.new()
			if img.load_jpg_from_buffer(img_body) == OK or img.load_png_from_buffer(img_body) == OK:
				callback.call(ImageTexture.create_from_image(img))
			else:
				callback.call(null)
		)
		img_request.request(face_url, PackedStringArray(), HTTPClient.METHOD_GET)
	)
	http_nav.request("https://api.bilibili.com/x/web-interface/nav", nav_headers, HTTPClient.METHOD_GET)

func get_csrf() -> String:
	return GdScriptFunc.get_data("AccountData", "bili_jct", "")
