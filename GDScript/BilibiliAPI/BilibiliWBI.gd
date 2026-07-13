class_name BilibiliWBI

static func sign_url(url: String, img_key: String, sub_key: String) -> String:
	var combined = img_key + sub_key
	if combined.length() < 64:
		push_error("WBI 密钥总长度不足 64")
		return url

	var wbi_key = ""
	for idx in BilibiliConstants.MIXIN_KEY_ENC_TAB:
		wbi_key += combined[idx]

	var uri = url.replace("https://api.bilibili.com", "")
	var parts = uri.split("?", false, 1)
	var base = parts[0]
	var query_string = parts[1] if parts.size() > 1 else ""

	var params = {}
	for param in query_string.split("&"):
		var kv = param.split("=")
		if kv.size() == 2:
			params[kv[0]] = kv[1].uri_decode()

	params.erase("w_rid")
	params.erase("wts")
	var wts = Time.get_unix_time_from_system()
	params["wts"] = wts

	var sorted_keys = params.keys()
	sorted_keys.sort()
	var sorted_query = ""
	for key in sorted_keys:
		if not sorted_query.is_empty():
			sorted_query += "&"
		sorted_query += key + "=" + str(params[key])
	var sign_str = sorted_query + wbi_key
	var w_rid = sign_str.md5_text()

	var final_parts = []
	for key in sorted_keys:
		var value = str(params[key]).uri_encode()
		final_parts.append(key + "=" + value)
	final_parts.append("w_rid=" + w_rid)
	return "https://api.bilibili.com" + base + "?" + "&".join(final_parts)
