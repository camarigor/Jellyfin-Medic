"""urlopen for calls that carry a token: python's urllib keeps every header, Authorization included, when it
follows a redirect, even to another host. This opener only sends it on to the same scheme and host."""
import urllib.parse
import urllib.request


class SameHostAuthorization(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        new = super().redirect_request(req, fp, code, msg, headers, newurl)
        if new is not None:
            old, to = urllib.parse.urlsplit(req.full_url), urllib.parse.urlsplit(new.full_url)
            if (old.scheme, old.netloc.lower()) != (to.scheme, to.netloc.lower()):
                new.remove_header("Authorization")
        return new


_opener = urllib.request.build_opener(SameHostAuthorization)


def urlopen(request, timeout):
    return _opener.open(request, timeout=timeout)
