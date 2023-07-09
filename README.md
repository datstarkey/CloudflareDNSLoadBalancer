# Cloudflare Dns LoadBalancer

A small service that will update dns zones in cloudflare based on available balancer loader IPs inside your kubernetes cluster.

# Installation

## Cloudflare token

You'll need a cloudflare token generated from cloudflare that has access to edit DNS zones.

## Helm

Helm is the recommended way to install

Run the commands:

```
helm repo add starkey-digital https://helm.starkeydigital.com
helm repo update
helm upgrade --install cloudflare-dns-loadbalancer starkey-digital/CloudflareDnsLoadBalancer --set cloudflare.token=YOUR_TOKEN
```

If you want to enable the cloudflare proxy you can do so by running

```
helm upgrade --install cloudflare-dns-loadbalancer starkey-digital/CloudflareDnsLoadBalancer  --set cloudflare.token=YOUR_TOKEN --set cloudflare.proxy=true
```

Or use your own values.yml

```
// Values.yml

cloudflare:
  token: YourToken
  proxy: true
```

```
helm upgrade --install cloudflare-dns-loadbalancer starkey-digital/CloudflareDnsLoadBalancer  --values values.yml
```

# Usage

Add the label:

```
cloudflaredns.kubernetes.io/hostname=somedomain.com
```

You can use any FQDN eg `some.subdomain.somedomain.com`

The app will detect updates to services and will add/remove A records for any loadbalancer Ip's available on that service.

## Example

Traefik Service:

```
apiVersion: v1
kind: Service
metadata:
  name: traefik
  namespace: default
  labels:
    cloudflaredns.kubernetes.io/hostname: mydomain.com

// omitted for brevity

status:
  loadBalancer:
    ingress:
      - ip: 109.110.92.29
      - ip: 140.84.251.38
      - ip: 100.113.127.10
```

Will ensure that the DNS zone has all the IPs in the loadBalancer set to A records

# Direct node routing

Some services you may only want the balance loader to only be used by certain nodes that pods are in.
This can prevent service disruption if a node goes down that has an external IP and that no pod is running in.

eg: pod runs on node1 with IP 109.110.92.29, but node2 goes down with ip 140.84.251.38, before the service can update the dns records an A record will be wrong for a short period of time and may lead to a small service disruption

by setting the label on the service:

```
cloudflaredns.kubernetes.io/exact-match=true
```

The application will check where the pods are running, and if they are in nodes with an external IP, will only set the A records to those nodes.

This can be handy with exposing TCP applications by port when they are required to run on a specific node.
