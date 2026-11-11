COPY (
    SELECT video.external_exercise_id AS "Referencia",
           job.youtube_id AS "YouTubeId",
           string_agg(DISTINCT asset.rendition, ';' ORDER BY asset.rendition) AS "ReadyHeights"
    FROM videos.videos video
    JOIN videos.video_import_jobs job ON job.video_id = video.id
    JOIN videos.video_assets asset ON asset.video_id = video.id
    WHERE video.owner_partner_id IS NULL AND video.status = 'active'
      AND asset.status = 'ready' AND asset.size_bytes > 0 AND asset.duration_ms > 0
      AND asset.language = 'es' AND asset.content_type = 'video/mp4'
      AND asset.rendition IN ('1080p', '720p', '480p', '360p')
    GROUP BY video.external_exercise_id, job.youtube_id
    ORDER BY video.external_exercise_id, job.youtube_id
) TO STDOUT WITH CSV HEADER;
