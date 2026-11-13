# Peppol/EN16931 rule checker sidecar (phorm = phive + official rules). Private network only.
# Pinned tag: a rule update is a tag bump, never `latest`.
FROM phelger/phorm:2.2.13

# The base image sets CATALINA_OPTS itself, so append rather than replace.
ENV CATALINA_OPTS="${CATALINA_OPTS} -XX:MaxRAMPercentage=60 -XX:+UseSerialGC -XX:TieredStopAtLevel=1 -Xss512k -XX:MaxMetaspaceSize=128m"

# phorm answers a failed validation with 400 by default; the checker client reads 200 + body.
ENV PHORM_API_RESPONSE_ONFAILURE_HTTP400=false

# The image has no curl or wget, so probe /ping over a raw TCP socket with bash.
# For local/compose only: Railway ignores Docker HEALTHCHECK and probes its own PORT.
HEALTHCHECK --interval=15s --timeout=5s --start-period=120s --retries=5   CMD ["bash", "-c", "exec 3<>/dev/tcp/localhost/8080 && printf 'GET /ping HTTP/1.0\r\n\r\n' >&3 && head -n1 <&3 | grep -q ' 200'"]
