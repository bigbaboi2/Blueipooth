package com.blueipooth.service

import android.service.notification.NotificationListenerService

/**
 * Lightweight NotificationListenerService enabling MediaSessionManager.getActiveSessions()
 * so Blueipooth can display and control active phone media (YouTube, Spotify, etc.) from PC.
 */
class BlueipoothMediaListener : NotificationListenerService()
