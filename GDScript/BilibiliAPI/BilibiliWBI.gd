class_name BilibiliWBI
static func sign_url(url: String, img_key: String, sub_key: String) -> String:
	# 1. 生成混合密钥 (mixin_key)
	var combined = img_key + sub_key
	if combined.length() < 64:
		push_error("[WBI] 密钥总长度不足 64，无法签名")
		return url

	var mixin_key = ""
	for idx in BilibiliConstants.MIXIN_KEY_ENC_TAB:
		mixin_key += combined[idx]
	mixin_key = mixin_key.substr(0, 32)  # 取前 32 位
	var uri = url.replace("https://api.bilibili.com", "")
	var parts = uri.split("?", false, 1)
	var base = parts[0]
	var query_string = parts[1] if parts.size() > 1 else ""

	var params = {}
	for param in query_string.split("&"):
		var kv = param.split("=", true, 1)
		if kv.size() == 2:
			# 保留原始值，不做解码
			params[kv[0]] = kv[1]
	params.erase("w_rid")
	params.erase("wts")
	var wts = int(Time.get_unix_time_from_system())
	params["wts"] = str(wts)
	var sorted_keys = params.keys()
	sorted_keys.sort()
	var sorted_query_for_sign = ""
	for key in sorted_keys:
		if not sorted_query_for_sign.is_empty():
			sorted_query_for_sign += "&"
		sorted_query_for_sign += key + "=" + str(params[key]).uri_encode()
	var sign_str = sorted_query_for_sign + mixin_key
	var w_rid = sign_str.md5_text()
	var final_parts = []
	for key in sorted_keys:
		final_parts.append(key + "=" + str(params[key]).uri_encode())
	final_parts.append("w_rid=" + w_rid)

	return "https://api.bilibili.com" + base + "?" + "&".join(final_parts)
