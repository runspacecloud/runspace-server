class RunSpaceError(Exception):
    """Base exception for RunSpace SDK."""
    pass

class AuthError(RunSpaceError):
    """Invalid or expired bot token."""
    pass

class PermissionError(RunSpaceError):
    """Bot lacks permission for this action."""
    pass

class NotFoundError(RunSpaceError):
    """Resource not found."""
    pass

class RateLimitError(RunSpaceError):
    """Bot is being rate limited."""
    pass
