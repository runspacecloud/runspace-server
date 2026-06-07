import requests
from .exceptions import AuthError, PermissionError, NotFoundError, RateLimitError, RunSpaceError


class HttpClient:
    def __init__(self, base_url: str, token: str):
        self.base_url = base_url.rstrip("/")
        self.token = token
        self.session = requests.Session()
        self.session.headers.update({
            "Authorization": f"Bot {token}",
            "Content-Type": "application/json",
            "User-Agent": "RunSpaceBot/0.1.0"
        })

    def _handle_response(self, resp: requests.Response):
        if resp.status_code == 401:
            raise AuthError("Invalid or expired bot token.")
        if resp.status_code == 403:
            raise PermissionError("Bot lacks permission for this action.")
        if resp.status_code == 404:
            raise NotFoundError("Resource not found.")
        if resp.status_code == 429:
            raise RateLimitError("Rate limited. Slow down.")
        if not resp.ok:
            try:
                msg = resp.json().get("message", resp.text)
            except Exception:
                msg = resp.text
            raise RunSpaceError(f"HTTP {resp.status_code}: {msg}")
        if resp.content:
            try:
                return resp.json()
            except Exception:
                return resp.text
        return None

    def get(self, path: str, **kwargs):
        resp = self.session.get(self.base_url + path, **kwargs)
        return self._handle_response(resp)

    def post(self, path: str, json=None, **kwargs):
        resp = self.session.post(self.base_url + path, json=json, **kwargs)
        return self._handle_response(resp)

    def delete(self, path: str, **kwargs):
        resp = self.session.delete(self.base_url + path, **kwargs)
        return self._handle_response(resp)

    def patch(self, path: str, json=None, **kwargs):
        resp = self.session.patch(self.base_url + path, json=json, **kwargs)
        return self._handle_response(resp)
