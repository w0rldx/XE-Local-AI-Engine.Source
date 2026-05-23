# Stage 1: Build the React app
FROM node:24.14-alpine AS build

# Set working directory
WORKDIR /app

# Copy the rest of the application code
COPY . ./

# install dependencies
RUN npm install

# Build the React app
RUN npm run build

# Stage 2: Serve the React app with Nginx
FROM nginx:1.29.5-alpine AS runtime

# Set working directory
WORKDIR /etc/nginx

# Copy build output from the previous stage to the Nginx web directory
COPY --from=build /app/dist /usr/share/nginx/html

# Copy custom Nginx configuration
COPY nginx.conf /etc/nginx/conf.d/default.conf

# Expose port 80
EXPOSE 80

# ---------------------------------------------------------------------------
# this block ensures that the application runs as a non-root user

# Create required directories with proper permissions
RUN mkdir -p /var/cache/nginx /var/run /var/log/nginx \
    && chmod 777 /var/cache/nginx /var/run /var/log/nginx

# Set up volumes for directories Nginx needs write access to
VOLUME ["/var/cache/nginx", "/var/run", "/var/log/nginx"]

# Create a non-root user and group
RUN addgroup --system appgroup && adduser --system --ingroup appgroup appuser

# Change ownership of writable directories
RUN chown -R appuser:appgroup /var/cache/nginx /var/run /var/log/nginx

# Switch to the non-root user
USER appuser
# ---------------------------------------------------------------------------

# Start Nginx
CMD ["nginx", "-g", "daemon off;"]
