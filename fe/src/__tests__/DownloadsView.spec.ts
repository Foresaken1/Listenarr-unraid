/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
import { describe, it, beforeEach, expect, vi } from 'vitest'
import { mount } from '@vue/test-utils'

describe('DownloadsView mobile virtualization', () => {
  beforeEach(() => {
    vi.resetModules()
    vi.clearAllMocks()
    vi.stubGlobal(
      'matchMedia',
      vi.fn().mockImplementation(() => ({
        matches: true,
        media: '(max-width: 768px)',
        onchange: null,
        addListener: vi.fn(),
        removeListener: vi.fn(),
        addEventListener: vi.fn(),
        removeEventListener: vi.fn(),
        dispatchEvent: vi.fn(),
      })),
    )
  })

  it('renders the full downloads list without virtualization on mobile', async () => {
    const activeDownloads = Array.from({ length: 30 }, (_, index) => ({
      id: `download-${index + 1}`,
      title: `Download ${index + 1}`,
      artist: `Artist ${index + 1}`,
      album: `Album ${index + 1}`,
      status: 'Downloading',
      progress: 25,
      totalSize: 1024,
      downloadedSize: 256,
      downloadClientId: 'qbittorrent',
      startedAt: new Date().toISOString(),
      finalPath: index % 2 === 0 ? `D:\\Downloads\\Download ${index + 1}` : '',
      errorMessage: '',
    }))

    vi.doMock('@/stores/downloads', () => ({
      useDownloadsStore: () => ({
        isLoading: false,
        activeDownloads,
        completedDownloads: [],
        failedDownloads: [],
        loadDownloads: vi.fn(async () => undefined),
        cancelDownload: vi.fn(async () => undefined),
      }),
    }))

    vi.doMock('@/services/toastService', () => ({
      useToast: () => ({
        success: vi.fn(),
        error: vi.fn(),
        info: vi.fn(),
      }),
    }))

    vi.doMock('@/services/errorTracking', () => ({
      errorTracking: {
        captureException: vi.fn(),
      },
    }))

    vi.doMock('@/utils/logger', () => ({
      logger: {
        warn: vi.fn(),
      },
    }))

    vi.doMock('@/services/api', () => ({
      apiService: {
        getCachedAnnounces: vi.fn(async () => ({ announces: [] })),
      },
    }))

    const { default: DownloadsView } = await import('@/views/activity/DownloadsView.vue')
    const wrapper = mount(DownloadsView, {
      global: {
        stubs: {
          CustomSelect: true,
          EmptyState: true,
          ProgressBar: true,
          InspectTorrentModal: true,
        },
      },
    })

    await new Promise((resolve) => setTimeout(resolve, 10))

    expect(wrapper.find('.downloads-list-container').classes()).toContain('is-static')
    expect(wrapper.find('.downloads-list.is-static').exists()).toBe(true)
    expect(wrapper.findAll('.download-card')).toHaveLength(30)

    wrapper.unmount()
  })

  it('queues a retry for a blocked import and refreshes downloads', async () => {
    const blockedDownload = {
      id: 'download-blocked',
      title: 'Fight Club',
      artist: 'Chuck Palahniuk',
      album: '',
      status: 'ImportBlocked',
      progress: 100,
      totalSize: 1024,
      downloadedSize: 1024,
      downloadClientId: 'qbittorrent',
      startedAt: new Date().toISOString(),
      finalPath: '',
      errorMessage: 'Import blocked',
    }
    const loadDownloads = vi.fn(async () => undefined)
    const retryBlockedImport = vi.fn(async () => ({
      message: 'Import retry queued',
      id: blockedDownload.id,
      jobId: 'job-1',
      status: 'Completed',
    }))
    const success = vi.fn()

    vi.doMock('@/stores/downloads', () => ({
      useDownloadsStore: () => ({
        isLoading: false,
        activeDownloads: [],
        completedDownloads: [],
        failedDownloads: [blockedDownload],
        loadDownloads,
        cancelDownload: vi.fn(async () => undefined),
      }),
    }))
    vi.doMock('@/services/toastService', () => ({
      useToast: () => ({ success, error: vi.fn(), info: vi.fn() }),
    }))
    vi.doMock('@/services/errorTracking', () => ({
      errorTracking: { captureException: vi.fn() },
    }))
    vi.doMock('@/utils/logger', () => ({ logger: { warn: vi.fn() } }))
    vi.doMock('@/services/api', () => ({
      apiService: {
        getCachedAnnounces: vi.fn(async () => ({ announces: [] })),
        retryBlockedImport,
      },
    }))

    const { default: DownloadsView } = await import('@/views/activity/DownloadsView.vue')
    const wrapper = mount(DownloadsView, {
      global: {
        stubs: {
          CustomSelect: true,
          EmptyState: true,
          ProgressBar: true,
          InspectTorrentModal: true,
        },
      },
    })

    await wrapper.get('.tab-button:nth-child(3)').trigger('click')
    await wrapper.get('.action-button.retry').trigger('click')

    expect(retryBlockedImport).toHaveBeenCalledWith(blockedDownload.id)
    expect(loadDownloads).toHaveBeenCalled()
    expect(success).toHaveBeenCalledWith('Import queued', 'Fight Club will be retried')
    wrapper.unmount()
  })
})
